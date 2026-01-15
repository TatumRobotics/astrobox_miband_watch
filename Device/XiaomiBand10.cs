using Google.Protobuf;
using Microsoft.Extensions.Logging;
using Protocol;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using XiaomiAstroBoxCSharp.Authentication;
using XiaomiAstroBoxCSharp.Bluetooth;
using XiaomiAstroBoxCSharp.Protocol;

namespace XiaomiAstroBoxCSharp.Device;

public class XiaomiBand10 : IDisposable
{
    public enum XiaomiBand10State
    {
        Created = 0,
        TransportConfigured = 1,
        Authenticating = 2,
        Authenticated = 3,
        Disposed = 4
    }

    private readonly ILogger<XiaomiBand10> _logger;
    private readonly BluetoothSppClient _bluetooth;
    private readonly AuthenticationHandler _authHandler;
    private readonly PacketProcessor _packetProcessor;
    private readonly CancellationTokenSource _lifetimeCts = new();
    private readonly bool _diagnosticLogging;
    private bool _disposed;
    private XiaomiBand10State _state = XiaomiBand10State.Created;

    private readonly object _txLock = new();
    private readonly Dictionary<byte, byte[]> _outboundBySeq = new();
    private readonly Dictionary<byte, int> _nakRetriesBySeq = new();
    private const int MaxNakRetriesPerSeq = 3;
    private const int MaxOutboundCacheEntries = 32;

    private TaskCompletionSource<bool> _sarHandshakeTcs;
    private IDisposable _cmdSubscription;
    private IDisposable _internalAckSubscription;
    private IDisposable _internalNakSubscription;

    // Sequence tracking for transmit
    private byte _txSeq = 0;
    
    private IDisposable _ackSubscription;
    private IDisposable _packetSubscription;
    
    // Generic request tracking
    private interface IPendingRequest
    {
        uint MessageId { get; }
        void TryHandle(WearPacket packet);
        void Cancel(Exception ex);
    }

    private sealed class PendingRequest<T> : IPendingRequest
    {
        public uint MessageId { get; }
        private readonly ILogger _logger;
        private readonly TaskCompletionSource<T> _tcs;
        private readonly Func<WearPacket, T> _parser;

        public PendingRequest(uint messageId, Func<WearPacket, T> parser, ILogger logger)
        {
            MessageId = messageId;
            _parser = parser ?? throw new ArgumentNullException(nameof(parser));
            _logger = logger;
            _tcs = new TaskCompletionSource<T>();
        }

        public Task<T> Task => _tcs.Task;

        public void TryHandle(WearPacket packet)
        {
            try
            {
                var result = _parser(packet);
                _tcs.TrySetResult(result);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error processing response for ID={Id}", MessageId);
                _tcs.TrySetException(ex);
            }
        }

        public void Cancel(Exception ex)
        {
            _tcs.TrySetException(ex);
        }
    }
    
    private readonly Dictionary<uint, IPendingRequest> _pendingRequests = [];

    public XiaomiBand10State State => _state;

    public BluetoothSppClient.ConnectionStatus GetBluetoothConnectionStatus()
    {
        return _bluetooth.GetConnectionStatus();
    }

    public XiaomiBand10(
        ILogger<XiaomiBand10> logger,
        BluetoothSppClient bluetooth,
        string authKey,
        ILoggerFactory loggerFactory,
        bool diagnosticLogging = false)
    {
        _logger = logger;
        _bluetooth = bluetooth;
        _diagnosticLogging = diagnosticLogging;

        var authLogger = loggerFactory.CreateLogger<AuthenticationHandler>();
        _authHandler = new AuthenticationHandler(authLogger, authKey, SendPacketAsync, diagnosticLogging: diagnosticLogging);

        var processorLogger = loggerFactory.CreateLogger<PacketProcessor>();
        _packetProcessor = new PacketProcessor(processorLogger, _authHandler, SendAcknowledgementAsync, diagnosticLogging: diagnosticLogging);

        // Internal protocol handlers (always-on for this device instance)
        _cmdSubscription = _packetProcessor.RegisterCmdReceived(OnCmdReceived);
        _internalAckSubscription = _packetProcessor.RegisterAckReceived(OnInternalAckReceived);
        _internalNakSubscription = _packetProcessor.RegisterNakReceived(OnInternalNakReceived);

        _bluetooth.SetDataListener(async (byte[] data, string error) =>
        {
            if (!string.IsNullOrEmpty(error))
            {
                _logger.LogError("Bluetooth error: {Error}", error);
                return;
            }
            await _packetProcessor.OnDataReceived(data);
        });
        
        _packetSubscription = _packetProcessor.RegisterPacketReceived(OnPacketReceived);
    }

    private void OnCmdReceived(L1CmdPacket cmd)
    {
        if (cmd == null) return;
        if (cmd.Cmd == CmdCode.CmdL1StartRsp)
        {
            _sarHandshakeTcs?.TrySetResult(true);
        }
    }

    private void OnInternalAckReceived(byte seq)
    {
        lock (_txLock)
        {
            _outboundBySeq.Remove(seq);
            _nakRetriesBySeq.Remove(seq);
        }
    }

    private void OnInternalNakReceived(byte seq)
    {
        if (_disposed) return;

        byte[] dataToResend = null;
        int attempt;

        lock (_txLock)
        {
            if (!_outboundBySeq.TryGetValue(seq, out dataToResend))
            {
                _logger.LogWarning("Received NAK for seq={Seq} but no cached outbound packet exists", seq);
                return;
            }

            _nakRetriesBySeq.TryGetValue(seq, out attempt);
            attempt++;
            _nakRetriesBySeq[seq] = attempt;
        }

        if (attempt > MaxNakRetriesPerSeq)
        {
            _logger.LogError("Seq={Seq} exceeded max NAK retries ({Max}). Giving up on resend.", seq, MaxNakRetriesPerSeq);
            return;
        }

        _logger.LogWarning("Resending seq={Seq} after NAK (attempt {Attempt}/{Max})", seq, attempt, MaxNakRetriesPerSeq);
        _ = Task.Run(async () =>
        {
            try
            {
                await _bluetooth.SendAsync(dataToResend, _lifetimeCts.Token);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to resend seq={Seq}", seq);
            }
        });
    }

    private void SetState(XiaomiBand10State newState)
    {
        if (_state == newState) return;
        _logger.LogInformation("State transition: {Old} -> {New}", _state, newState);
        _state = newState;
    }

    private void EnsureNotDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(XiaomiBand10));
    }
    
    private void HandleSystemPacket(WearPacket packet)
    {
        _logger.LogDebug("System packet received in XiaomiBand10: ID={Id}", packet.Id);
        
        if (packet.System != null)
        {
            _logger.LogDebug($"System packet: {packet.System}");
        }
        
        // Check if there's a pending request for this message ID
        IPendingRequest request;
        lock (_pendingRequests)
        {
            if (_pendingRequests.TryGetValue(packet.Id, out request))
            {
                _pendingRequests.Remove(packet.Id);
            }
            else
            {
                return;
            }
        }

        // Complete outside lock
        request.TryHandle(packet);
        _logger.LogDebug("Request for ID={Id} completed successfully", packet.Id);
    }

    private void OnPacketReceived(WearPacket packet)
    {
        switch (packet.Type)
        {
            case WearPacket.Types.Type.System:
                HandleSystemPacket(packet);
                break;
            default:
                break;
        }
    }

    public void OnAckReceived(Action<byte> callback)
    {
        _ackSubscription?.Dispose();
        _ackSubscription = _packetProcessor.RegisterAckReceived(callback);
    }

    public async Task<bool> AuthenticateAsync(CancellationToken ct = default)
    {
        EnsureNotDisposed();
        _logger.LogInformation("Starting authentication process...");

        _sarHandshakeTcs = new TaskCompletionSource<bool>();
        await SendTransportLayerConfigAsync(ct);
        SetState(XiaomiBand10State.TransportConfigured);

        // Wait for L1StartRsp to ensure SAR handshake is complete before auth begins.
        using (var sarTimeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetimeCts.Token))
        {
            sarTimeoutCts.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                await _sarHandshakeTcs.Task.WaitAsync(sarTimeoutCts.Token);
                _logger.LogInformation("SAR handshake complete (L1StartRsp received)");
            }
            catch (OperationCanceledException)
            {
                _logger.LogError("Timed out waiting for L1StartRsp (SAR handshake)");
                return false;
            }
        }
        
        // Delegate to authentication handler
        SetState(XiaomiBand10State.Authenticating);
        var ok = await _authHandler.AuthenticateAsync(ct);
        if (ok) SetState(XiaomiBand10State.Authenticated);
        return ok;
    }

    /// <summary>
    /// Checks whether the Bluetooth transport is connected, and optionally sends a protocol-level ping
    /// (requires authentication) to confirm the watch is responsive.
    /// </summary>
    public async Task<bool> PingAsync(bool protocolPing = true, int timeoutSeconds = 2, CancellationToken ct = default)
    {
        EnsureNotDisposed();

        if (!_bluetooth.IsConnected)
            return false;

        if (!protocolPing)
            return true;

        if (!_authHandler.IsAuthenticated)
        {
            // Can't reliably ping at the protocol level without completing auth.
            return true;
        }

        try
        {
            // Lightweight ping: ask for device info and accept any valid response.
            await SendRequestAsync<bool>(
                WearPacket.Types.Type.System,
                (uint)SystemMessage.Types.SystemID.GetDeviceInfo,
                packet => packet.System?.DeviceInfo != null,
                timeoutSeconds: timeoutSeconds,
                ct: ct);

            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch
        {
            return false;
        }
    }

    public async Task VibrateAsync(List<VibrationSegment> segments, CancellationToken ct = default)
    {
        EnsureNotDisposed();
        if (!_authHandler.IsAuthenticated)
            throw new InvalidOperationException("Device not authenticated");

        _logger.LogInformation("Sending vibration pattern with {Count} segments", segments.Count);

        // Convert VibrationSegment list to protobuf VibratorEffect.Segment list
        var protoSegments = new Google.Protobuf.Collections.RepeatedField<VibratorEffect.Types.Segment>();
        foreach (var segment in segments)
        {
            protoSegments.Add(new VibratorEffect.Types.Segment
            {
                On = segment.On,
                Duration = (uint)segment.Duration,
                Strength = (uint)segment.Strength
            });
        }

        // Create VibratorEffect
        var vibratorEffect = new VibratorEffect
        {
            Segments = { protoSegments }
        };

        // Create SystemMessage with VibratorEffect
        var systemMessage = new SystemMessage
        {
            VibratorEffect = vibratorEffect
        };

        // Create WearPacket with System type and TEST_VIBRATOR id
        var packet = new WearPacket
        {
            Type = WearPacket.Types.Type.System,
            Id = (uint)SystemMessage.Types.SystemID.TestVibrator,
            System = systemMessage
        };

        _logger.LogDebug("Sending TEST_VIBRATOR packet with {Count} segments", segments.Count);
        await SendPacketAsync(packet, encrypt: true, ct);
        _logger.LogInformation("Vibration packet sent successfully");
    }

    private async Task SendPacketAsync(WearPacket packet, bool encrypt = true, CancellationToken ct = default)
    {
        EnsureNotDisposed();
        _logger.LogDebug("Encoding WearPacket to protobuf: Type={Type}, Id={Id}", packet.Type, packet.Id);
        var pbData = packet.ToByteArray();
        _logger.LogDebug("Protobuf data ({Length} bytes)", pbData.Length);
        if (XiaomiAstroBoxCSharp.Protocol.SensitiveLogging.Enabled(_logger, _diagnosticLogging))
        {
            _logger.LogTrace("Protobuf bytes: {Data}", XiaomiAstroBoxCSharp.Protocol.SensitiveLogging.BytesToHex(pbData));
        }

        var shouldEncrypt = encrypt && _authHandler.Cipher != null;
        _logger.LogDebug("Creating L2 packet: encrypt={Encrypt}", shouldEncrypt);
        
        var l2Packet = new L2Packet(
            L2Channel.Pb,
            shouldEncrypt ? L2OpCode.WriteEnc : L2OpCode.Write,
            shouldEncrypt ? _authHandler.Cipher!.Encrypt(pbData) : pbData
        );

        if (shouldEncrypt)
        {
            _logger.LogDebug("L2 encrypted payload ({Length} bytes)", l2Packet.Payload.Length);
            if (XiaomiAstroBoxCSharp.Protocol.SensitiveLogging.Enabled(_logger, _diagnosticLogging))
            {
                _logger.LogTrace("L2 encrypted payload: {Data}", XiaomiAstroBoxCSharp.Protocol.SensitiveLogging.BytesToHex(l2Packet.Payload));
            }
        }

        var seq = _txSeq++;
        _logger.LogDebug("Creating L1 packet with seq={Seq}", seq);
        var l1Packet = l2Packet.ToL1(seq);
        
        var l1Bytes = l1Packet.ToBytes();
        _logger.LogDebug("Sending L1 packet ({Length} bytes)", l1Bytes.Length);
        if (XiaomiAstroBoxCSharp.Protocol.SensitiveLogging.Enabled(_logger, _diagnosticLogging))
        {
            _logger.LogTrace("Sending L1 bytes: {Data}", XiaomiAstroBoxCSharp.Protocol.SensitiveLogging.BytesToHex(l1Bytes));
        }

        // Cache outbound bytes for potential resend on NAK.
        lock (_txLock)
        {
            if (_outboundBySeq.Count >= MaxOutboundCacheEntries)
            {
                // Remove the oldest entry by insertion order heuristic: pick the lowest retry entry / first key.
                // (This is a simple bounded cache; we mainly need recent packets.)
                var keyToRemove = _outboundBySeq.Keys.First();
                _outboundBySeq.Remove(keyToRemove);
                _nakRetriesBySeq.Remove(keyToRemove);
            }
            _outboundBySeq[seq] = l1Bytes;
        }

        await _bluetooth.SendAsync(l1Bytes, ct);
        _logger.LogDebug("Packet sent successfully");
    }

    private async Task SendTransportLayerConfigAsync(CancellationToken ct = default)
    {
        EnsureNotDisposed();
        _logger.LogInformation("Sending L1StartReq (SAR handshake)...");

        // CmdL1StartReq = start communicating
        var cmd = new L1CmdPacket(CmdCode.CmdL1StartReq)
        {
            Config = new Dictionary<byte, byte[]>
            {
                // protocol version 1.0.0
                [L1CmdPacket.CONFIG_TYPE_VERSION] = [1, 0, 0],
                // set max packet size to 65,535 bytes (FFFF in hex)
                [L1CmdPacket.CONFIG_TYPE_MPS] = BitConverter.GetBytes((ushort)0xFFFF),
                // transmit window size to 16 packets long
                // so 16 packets can be sent before getting an acknowledgement
                [L1CmdPacket.CONFIG_TYPE_TX_WIN] = BitConverter.GetBytes((ushort)16),
                // 15 second timeout to wait for acknowledgements
                [L1CmdPacket.CONFIG_TYPE_SEND_TIMEOUT] = BitConverter.GetBytes((ushort)15000),
                // i don't know what device type is but 0 works!
                [L1CmdPacket.CONFIG_TYPE_DEVICE_TYPE] = [0],
            }
        };
        
        var cmdBytes = cmd.ToBytes();
        _logger.LogDebug("L1StartReq payload ({Length} bytes): {Data}", cmdBytes.Length, BitConverter.ToString(cmdBytes));
        
        var cmdPacket = new L1Packet(L1DataType.Cmd, false, 0, cmdBytes);
        var packetBytes = cmdPacket.ToBytes();
        
        _logger.LogDebug("Sending L1 CMD packet ({Length} bytes): {Data}", packetBytes.Length, BitConverter.ToString(packetBytes));
        await _bluetooth.SendAsync(packetBytes, ct);
        _logger.LogInformation("Sent L1StartReq");
    }

    private async Task SendAcknowledgementAsync(byte seq)
    {
        if (_disposed) return;
        _logger.LogDebug("Sending ACK for seq={Seq}", seq);
        var ackPacket = new L1Packet(L1DataType.Ack, false, seq, Array.Empty<byte>());
        var ackBytes = ackPacket.ToBytes();
        _logger.LogTrace("ACK packet ({Length} bytes): {Data}", ackBytes.Length, BitConverter.ToString(ackBytes));
        await _bluetooth.SendAsync(ackBytes);
    }

    public async Task<T> SendRequestAsync<T>(
        WearPacket.Types.Type messageType,
        uint messageId,
        Func<WearPacket, T> parser,
        int timeoutSeconds = 10,
        CancellationToken ct = default)
    {
        EnsureNotDisposed();
        if (!_authHandler.IsAuthenticated)
            throw new InvalidOperationException("Device not authenticated");

        _logger.LogInformation("Sending request: Type={Type}, ID={Id}", messageType, messageId);

        // Create a new pending request
        var request = new PendingRequest<T>(messageId, parser, _logger);

        lock (_pendingRequests)
        {
            if (_pendingRequests.ContainsKey(messageId))
            {
                throw new InvalidOperationException($"A request for message ID {messageId} is already pending");
            }
            _pendingRequests[messageId] = request;
        }

        var packet = new WearPacket
        {
            Type = messageType,
            Id = messageId
        };

        try
        {
            await SendPacketAsync(packet, encrypt: true, ct);
            _logger.LogDebug("Request sent, waiting for response...");

            // Wait for the response with timeout
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetimeCts.Token);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

            var result = await request.Task.WaitAsync(timeoutCts.Token);
            _logger.LogInformation("Response received for ID={Id}", messageId);
            return result;
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Request timed out or was cancelled for ID={Id}", messageId);
            lock (_pendingRequests)
            {
                _pendingRequests.Remove(messageId);
            }
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Request failed for ID={Id}", messageId);
            lock (_pendingRequests)
            {
                _pendingRequests.Remove(messageId);
            }
            throw;
        }
    }

    public async Task<uint> RequestBatteryPercentAsync(CancellationToken ct = default)
    {
        EnsureNotDisposed();
        _logger.LogInformation("Requesting battery percent...");

        return await SendRequestAsync<uint>(
            WearPacket.Types.Type.System,
            (uint)SystemMessage.Types.SystemID.GetDeviceStatus,
            packet =>
            {
                _logger.LogDebug("Device status found!");
                var deviceStatus = packet.System?.DeviceStatus;
                if (deviceStatus == null)
                {
                    _logger.LogWarning("Couldn't find .DeviceStatus in wear status packet!");
                    return 0;
                }
                var battery = deviceStatus.Battery;
                if (battery == null)
                {
                    _logger.LogWarning("Couldn't find .Battery in device status packet!");
                    return 0;
                }
                var capacity = battery.Capacity;
                var chargeState = battery.ChargeStatus;
                Console.WriteLine($"Battery: {capacity}%, charge status: {chargeState}");
                return capacity;
            },
            timeoutSeconds: 10,
            ct: ct);
    }

    public async Task<bool> RequestIsWearingWatchAsync(CancellationToken ct = default)
    {
        EnsureNotDisposed();
        _logger.LogInformation("Requesting wear status...");

        return await SendRequestAsync(
            WearPacket.Types.Type.System,
            (uint)SystemMessage.Types.SystemID.GetWearStatus,
            packet =>
            {
                _logger.LogDebug("Got basic status response");
                if (packet.System == null)
                {
                    _logger.LogWarning("Couldn't find .System in wear status packet!");
                    return false;
                }

                // Prefer explicit wear_status if present
                if (packet.System.WearStatus != 0)
                {
                    var wearStatus = packet.System.WearStatus;
                    return wearStatus == BasicStatus.Types.Wearing.On;
                }

                // Fallback to report_basic_status
                var report = packet.System.ReportBasicStatus;
                if (report != null && report.Wearing != 0)
                {
                    return report.Wearing == BasicStatus.Types.Wearing.On;
                }

                _logger.LogWarning("No wear status present in response");
                return false;
            },
            timeoutSeconds: 10,
            ct: ct);
    }
    
    public async Task SetWatchTimeAsync(
        DateTime currentTime,
        TimeZoneInfo timeZone = null,
        bool? is12Hours = null,
        CancellationToken ct = default)
    {
        EnsureNotDisposed();
        timeZone ??= TimeZoneInfo.Local;
        var offset = timeZone.GetUtcOffset(currentTime);
        var baseOffset = timeZone.BaseUtcOffset;

        // Offsets are in 15-minute increments per protocol docs/comments.
        int zoneOffset15 = (int)Math.Round(offset.TotalMinutes / 15.0);
        int dstOffset15 = (int)Math.Round((offset - baseOffset).TotalMinutes / 15.0);

        var dateMessage = new Date
        {
            Day = (uint)currentTime.Day,
            Month = (uint)currentTime.Month,
            Year = (uint)currentTime.Year
        };
        var timeMessage = new Time
        {
            Hour = (uint)currentTime.Hour,
            Minute = (uint)currentTime.Minute,
            Second = (uint)currentTime.Second,
            Millisecond = (uint)currentTime.Millisecond
        };

        var timeZoneMessage = new Timezone
        {
            DstOffset = dstOffset15,
            ZoneOffset = zoneOffset15,
            Name = timeZone.Id
        };

        var systemTimeMessage = new SystemTime
        {
            Date = dateMessage,
            Time = timeMessage,
            TimeZone = timeZoneMessage,
            Is12Hours = is12Hours ?? true,
        };
        /*
         * 
                .setTimezone(XiaomiProto.TimeZone.newBuilder()
                        .setZoneOffset(((now.get(Calendar.ZONE_OFFSET) / 1000) / 60) / 15)
                        .setDstOffset(((now.get(Calendar.DST_OFFSET) / 1000) / 60) / 15)
                        .setName(tz.getID())
                        .build())
        
        where tz is a Java timezone
         */
        var systemMessage = new SystemMessage
        {
            SystemTime = systemTimeMessage,
        };
        var packet = new WearPacket
        {
            Type = WearPacket.Types.Type.System,
            Id = (uint)SystemMessage.Types.SystemID.SetSystemTime,
            System = systemMessage,
        };
        await SendPacketAsync(packet, encrypt: true, ct);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        SetState(XiaomiBand10State.Disposed);

        _lifetimeCts.Cancel();
        _lifetimeCts.Dispose();

        // Detach from the shared Bluetooth client (caller owns its lifetime).
        _bluetooth.SetDataListener(null);

        _ackSubscription?.Dispose();
        _packetSubscription?.Dispose();
        _cmdSubscription?.Dispose();
        _internalAckSubscription?.Dispose();
        _internalNakSubscription?.Dispose();

        // Fail any pending requests promptly.
        lock (_pendingRequests)
        {
            foreach (var req in _pendingRequests.Values)
            {
                req.Cancel(new ObjectDisposedException(nameof(XiaomiBand10)));
            }
            _pendingRequests.Clear();
        }

        lock (_txLock)
        {
            _outboundBySeq.Clear();
            _nakRetriesBySeq.Clear();
        }

        GC.SuppressFinalize(this);
    }
}
