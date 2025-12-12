using Google.Protobuf;
using Microsoft.Extensions.Logging;
using Protocol;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using XiaomiAstroBoxCSharp.Authentication;
using XiaomiAstroBoxCSharp.Bluetooth;
using XiaomiAstroBoxCSharp.Protocol;

namespace XiaomiAstroBoxCSharp.Device;

public class XiaomiBand10 : IDisposable
{
    private readonly ILogger<XiaomiBand10> _logger;
    private readonly BluetoothSppClient _bluetooth;
    private readonly AuthenticationHandler _authHandler;
    private readonly PacketProcessor _packetProcessor;
    private readonly CancellationTokenSource _cts;

    // Sequence tracking for transmit
    private byte _txSeq = 0;
    
    private Action<byte> _onAckReceived;
    
    // Generic request tracking
    private class PendingRequest<T>
    {
        public TaskCompletionSource<T> Tcs { get; set; }
        public Func<WearPacket, T> Parser { get; set; }
    }
    
    private readonly Dictionary<uint, object> _pendingRequests = [];

    public XiaomiBand10(
        ILogger<XiaomiBand10> logger,
        BluetoothSppClient bluetooth,
        string authKey,
        ILoggerFactory loggerFactory)
    {
        _logger = logger;
        _bluetooth = bluetooth;
        _cts = new CancellationTokenSource();

        var authLogger = loggerFactory.CreateLogger<AuthenticationHandler>();
        _authHandler = new AuthenticationHandler(authLogger, authKey, SendPacketAsync);

        var processorLogger = loggerFactory.CreateLogger<PacketProcessor>();
        _packetProcessor = new PacketProcessor(processorLogger, _authHandler, SendAcknowledgementAsync);

        _bluetooth.SetDataListener(async (byte[] data, string error) =>
        {
            if (!string.IsNullOrEmpty(error))
            {
                _logger.LogError("Bluetooth error: {Error}", error);
                return;
            }
            await _packetProcessor.OnDataReceived(data);
        });
        
        _packetProcessor.OnPacketReceived(OnPacketReceived);
    }
    
    private void HandleSystemPacket(WearPacket packet)
    {
        _logger.LogDebug("System packet received in XiaomiBand10: ID={Id}", packet.Id);
        
        if (packet.System != null)
        {
            _logger.LogDebug($"System packet: {packet.System}");
        }
        
        // Check if there's a pending request for this message ID
        lock (_pendingRequests)
        {
            if (_pendingRequests.TryGetValue(packet.Id, out var requestObj))
            {
                _pendingRequests.Remove(packet.Id);
                
                // Use reflection to invoke the parser and set the result
                var requestType = requestObj.GetType();
                var parserProperty = requestType.GetProperty("Parser");
                var tcsProperty = requestType.GetProperty("Tcs");
                
                if (parserProperty != null && tcsProperty != null)
                {
                    try
                    {
                        var tcs = tcsProperty.GetValue(requestObj);

                        if (parserProperty.GetValue(requestObj) is Delegate parser && tcs != null)
                        {
                            var result = parser.DynamicInvoke(packet);
                            var setResultMethod = tcs.GetType().GetMethod("TrySetResult");
                            setResultMethod?.Invoke(tcs, [result]);
                            _logger.LogDebug("Request for ID={Id} completed successfully", packet.Id);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error processing response for ID={Id}", packet.Id);
                        var tcs = tcsProperty.GetValue(requestObj);
                        var setExceptionMethod = tcs?.GetType().GetMethod("TrySetException", new[] { typeof(Exception) });
                        setExceptionMethod?.Invoke(tcs, new object[] { ex });
                    }
                }
            }
        }
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
        _onAckReceived = callback;
        _packetProcessor.OnAckReceived(callback);
    }

    public async Task<bool> AuthenticateAsync(CancellationToken ct = default)
    {
        _logger.LogInformation("Starting authentication process...");

        await SendTransportLayerConfigAsync(ct);
        
        // Wait for L1StartRsp (give device time to respond)
        //_logger.LogDebug("Waiting for L1StartRsp...");
        //await Task.Delay(500, ct);

        // Delegate to authentication handler
        return await _authHandler.AuthenticateAsync(ct);
    }

    public async Task VibrateAsync(List<VibrationSegment> segments, CancellationToken ct = default)
    {
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
            // item is optional and not needed for TEST_VIBRATOR
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
        _logger.LogDebug("Encoding WearPacket to protobuf: Type={Type}, Id={Id}", packet.Type, packet.Id);
        var pbData = packet.ToByteArray();
        _logger.LogDebug("Protobuf data ({Length} bytes): {Data}", pbData.Length, BitConverter.ToString(pbData));

        var shouldEncrypt = encrypt && _authHandler.Cipher != null;
        _logger.LogDebug("Creating L2 packet: encrypt={Encrypt}", shouldEncrypt);
        
        var l2Packet = new L2Packet(
            L2Channel.Pb,
            shouldEncrypt ? L2OpCode.WriteEnc : L2OpCode.Write,
            shouldEncrypt ? _authHandler.Cipher!.Encrypt(pbData) : pbData
        );

        if (shouldEncrypt)
        {
            _logger.LogDebug("L2 encrypted payload ({Length} bytes): {Data}", 
                l2Packet.Payload.Length, BitConverter.ToString(l2Packet.Payload));
        }

        var seq = _txSeq++;
        _logger.LogDebug("Creating L1 packet with seq={Seq}", seq);
        var l1Packet = l2Packet.ToL1(seq);
        
        var l1Bytes = l1Packet.ToBytes();
        _logger.LogDebug("Sending L1 packet ({Length} bytes): {Data}", l1Bytes.Length, BitConverter.ToString(l1Bytes));
        await _bluetooth.SendAsync(l1Bytes, ct);
        _logger.LogDebug("Packet sent successfully");
    }

    private async Task SendTransportLayerConfigAsync(CancellationToken ct = default)
    {
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
        if (!_authHandler.IsAuthenticated)
            throw new InvalidOperationException("Device not authenticated");

        _logger.LogInformation("Sending request: Type={Type}, ID={Id}", messageType, messageId);

        // Create a new pending request
        var request = new PendingRequest<T>
        {
            Tcs = new TaskCompletionSource<T>(),
            Parser = parser
        };

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
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

            var result = await request.Tcs.Task.WaitAsync(timeoutCts.Token);
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
        _logger.LogInformation("Requesting battery percent...");

        return await SendRequestAsync(
            WearPacket.Types.Type.System,
            (uint)SystemMessage.Types.SystemID.ReportBasicStatus,
            packet =>
            {
                _logger.LogDebug("Got wear status!");
                if (packet.System == null)
                {
                    _logger.LogWarning("Couldn't find .System in wear status packet!");
                    return false;
                }
                var basicStatus = packet.System.ReportBasicStatus;
                if (basicStatus == null)
                {
                    _logger.LogWarning("Couldn't find basic status in packet for checking if user is wearing the watch.");
                    return false;
                }
                var isWearingWatch = (basicStatus.Wearing == BasicStatus.Types.Wearing.On);
                return isWearingWatch;
            },
            timeoutSeconds: 10,
            ct: ct);
    }
    
    public async Task SetWatchTimeAsync(CancellationToken ct = default)
    {
        var currentTime = DateTime.UtcNow;
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
        // TODO: make time zone match actual location!
        var timeZoneMessage = new Timezone
        {
            DstSaving = 1,
            Offset = -5,
            Id = "America/New_York",
            IdSpec = "EST5EDT"
        };
        var systemTimeMessage = new SystemTime
        {
            Date = dateMessage,
            Time = timeMessage,
            //TimeZone = timeZoneMessage,
            Is12Hours = true,
        };
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
        _cts.Cancel();
        _bluetooth.Dispose();
        GC.SuppressFinalize(this);
    }
}
