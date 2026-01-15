using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Protocol;
using XiaomiAstroBoxCSharp.Protocol;
using XiaomiAstroBoxCSharp.Authentication;

namespace XiaomiAstroBoxCSharp.Protocol;

/// <summary>
/// Handles processing of L1 and L2 packets received from the Mi Band device
/// </summary>
public class PacketProcessor
{
    private readonly ILogger<PacketProcessor> _logger;
    private readonly AuthenticationHandler _authHandler;
    private readonly Func<byte, Task> _sendAckFunc;
    private readonly PacketBuffer _packetBuffer;
    private readonly bool _diagnosticLogging;
    private readonly object _handlerLock = new();
    private readonly List<Action<byte>> _ackHandlers = new();
    private readonly List<Action<byte>> _nakHandlers = new();
    private readonly List<Action<WearPacket>> _packetHandlers = new();
    private readonly List<Action<L1CmdPacket>> _cmdHandlers = new();

    public PacketProcessor(
        ILogger<PacketProcessor> logger,
        AuthenticationHandler authHandler,
        Func<byte, Task> sendAckFunc,
        bool diagnosticLogging = false)
    {
        _logger = logger;
        _authHandler = authHandler;
        _sendAckFunc = sendAckFunc;
        _packetBuffer = new PacketBuffer(logger);
        _diagnosticLogging = diagnosticLogging;
    }

    private sealed class Subscription(Action unsubscribe) : IDisposable
    {
        private Action _unsubscribe = unsubscribe;
        public void Dispose()
        {
            var action = System.Threading.Interlocked.Exchange(ref _unsubscribe, null);
            action?.Invoke();
        }
    }

    /// <summary>
    /// Register a callback to be invoked when an ACK packet is received.
    /// Dispose the returned object to unsubscribe.
    /// </summary>
    public IDisposable RegisterAckReceived(Action<byte> callback)
    {
        if (callback == null) throw new ArgumentNullException(nameof(callback));
        lock (_handlerLock)
        {
            _ackHandlers.Add(callback);
        }
        return new Subscription(() =>
        {
            lock (_handlerLock)
            {
                _ackHandlers.Remove(callback);
            }
        });
    }

    /// <summary>
    /// Register a callback to be invoked when a NAK packet is received.
    /// Dispose the returned object to unsubscribe.
    /// </summary>
    public IDisposable RegisterNakReceived(Action<byte> callback)
    {
        if (callback == null) throw new ArgumentNullException(nameof(callback));
        lock (_handlerLock)
        {
            _nakHandlers.Add(callback);
        }
        return new Subscription(() =>
        {
            lock (_handlerLock)
            {
                _nakHandlers.Remove(callback);
            }
        });
    }

    /// <summary>
    /// Register a callback to be invoked when a packet is received.
    /// Dispose the returned object to unsubscribe.
    /// </summary>
    public IDisposable RegisterPacketReceived(Action<WearPacket> callback)
    {
        if (callback == null) throw new ArgumentNullException(nameof(callback));
        lock (_handlerLock)
        {
            _packetHandlers.Add(callback);
        }
        return new Subscription(() =>
        {
            lock (_handlerLock)
            {
                _packetHandlers.Remove(callback);
            }
        });
    }

    /// <summary>
    /// Register a callback to be invoked when an L1 CMD packet is received and parsed.
    /// Dispose the returned object to unsubscribe.
    /// </summary>
    public IDisposable RegisterCmdReceived(Action<L1CmdPacket> callback)
    {
        if (callback == null) throw new ArgumentNullException(nameof(callback));
        lock (_handlerLock)
        {
            _cmdHandlers.Add(callback);
        }
        return new Subscription(() =>
        {
            lock (_handlerLock)
            {
                _cmdHandlers.Remove(callback);
            }
        });
    }

    /// <summary>
    /// Clears all registered handlers (useful when tearing down a connection session).
    /// </summary>
    public void ClearHandlers()
    {
        lock (_handlerLock)
        {
            _ackHandlers.Clear();
            _nakHandlers.Clear();
            _packetHandlers.Clear();
            _cmdHandlers.Clear();
        }
    }

    public async Task OnDataReceived(byte[] data)
    {
        try
        {
            LogDataReceived(data);
            
            _packetBuffer.AddData(data);

            // Process complete L1 packets
            while (_packetBuffer.TryExtractPacket(out var l1Packet))
            {
                await ProcessL1PacketAsync(l1Packet);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing received data");
        }
    }

    private void LogDataReceived(byte[] data)
    {
        _logger.LogDebug("WATCH DATA RECEIVED: {Count} bytes", data.Length);
        if (SensitiveLogging.Enabled(_logger, _diagnosticLogging))
        {
            _logger.LogTrace("WATCH DATA: {Data}", SensitiveLogging.BytesToHex(data));
        }
    }

    private void LogPacketInfo(L1Packet packet, string prefix = "")
    {
        _logger.LogInformation("{Prefix}L1 packet: Type={Type}, Seq={Seq}, Length={Length}, Frx={Frx}", 
            prefix, packet.Type, packet.Seq, packet.Length, packet.Frx);
        _logger.LogDebug("L1 payload ({Length} bytes)", packet.Payload.Length);
        if (SensitiveLogging.Enabled(_logger, _diagnosticLogging))
        {
            _logger.LogTrace("L1 payload: {Data}", SensitiveLogging.BytesToHex(packet.Payload));
        }
    }

    private async Task ProcessL1PacketAsync(L1Packet l1Packet)
    {
        LogPacketInfo(l1Packet, "Received ");

        switch (l1Packet.Type)
        {
            case L1DataType.Data:
                await HandleDataPacketAsync(l1Packet);
                break;

            case L1DataType.Ack:
                HandleAckPacket(l1Packet);
                break;

            case L1DataType.Nak:
                HandleNakPacket(l1Packet);
                break;

            case L1DataType.Cmd:
                HandleCmdPacket(l1Packet);
                break;

            default:
                _logger.LogWarning("Received unknown L1 packet type: {Type}", l1Packet.Type);
                break;
        }
    }

    private async Task HandleDataPacketAsync(L1Packet l1Packet)
    {
        _logger.LogDebug("Processing DATA packet, seq={Seq}", l1Packet.Seq);
        await ProcessDataPacketAsync(l1Packet);
        
        _logger.LogDebug("Sending ACK for seq={Seq}", l1Packet.Seq);
        await _sendAckFunc(l1Packet.Seq);
    }

    private void HandleAckPacket(L1Packet l1Packet)
    {
        _logger.LogDebug("Received ACK for seq {Seq}", l1Packet.Seq);
        Action<byte>[] handlers;
        lock (_handlerLock)
        {
            handlers = _ackHandlers.ToArray();
        }
        foreach (var h in handlers)
        {
            try { h(l1Packet.Seq); }
            catch (Exception ex) { _logger.LogError(ex, "ACK handler threw"); }
        }
    }

    private void HandleNakPacket(L1Packet l1Packet)
    {
        _logger.LogWarning("Received NAK for seq {Seq}", l1Packet.Seq);
        Action<byte>[] handlers;
        lock (_handlerLock)
        {
            handlers = _nakHandlers.ToArray();
        }
        foreach (var h in handlers)
        {
            try { h(l1Packet.Seq); }
            catch (Exception ex) { _logger.LogError(ex, "NAK handler threw"); }
        }
    }

    private void HandleCmdPacket(L1Packet l1Packet)
    {
        _logger.LogDebug("Received CMD packet");
        ProcessCmdPacket(l1Packet);
    }

    private void ProcessCmdPacket(L1Packet l1Packet)
    {
        var cmdPacket = L1CmdPacket.FromBytes(l1Packet.Payload);
        if (cmdPacket == null)
        {
            _logger.LogWarning("Failed to parse L1 CMD packet");
            return;
        }

        _logger.LogInformation("L1 CMD: {Cmd}", cmdPacket.Cmd);

        // Notify handlers first so higher layers can coordinate state machines.
        Action<L1CmdPacket>[] handlers;
        lock (_handlerLock)
        {
            handlers = _cmdHandlers.ToArray();
        }
        foreach (var h in handlers)
        {
            try { h(cmdPacket); }
            catch (Exception ex) { _logger.LogError(ex, "CMD handler threw"); }
        }

        switch (cmdPacket.Cmd)
        {
            case CmdCode.CmdL1StartRsp:
                _logger.LogInformation("Received L1StartRsp - SAR handshake complete");
                foreach (var (key, value) in cmdPacket.Config)
                {
                    _logger.LogDebug("  Config 0x{Key:X2}: {Value}", key, BitConverter.ToString(value));
                }
                break;

            case CmdCode.CmdL1StopRsp:
                _logger.LogInformation("Received L1StopRsp");
                break;

            default:
                _logger.LogDebug("Received unknown CMD code: {Cmd}", cmdPacket.Cmd);
                break;
        }
    }

    private async Task ProcessDataPacketAsync(L1Packet l1Packet)
    {
        _logger.LogDebug("Parsing L2 packet from L1, cipher={Cipher}", _authHandler.Cipher != null ? "active" : "none");
        var l2Packet = L2Packet.FromL1(l1Packet, _authHandler.Cipher);
        if (l2Packet == null)
        {
            _logger.LogWarning("Failed to parse L2 packet from L1 payload");
            return;
        }

        _logger.LogInformation("L2 Packet: Channel={Channel}, OpCode={OpCode}, PayloadLength={Length}", 
            l2Packet.Channel, l2Packet.OpCode, l2Packet.Payload.Length);
        _logger.LogDebug("L2 payload ({Length} bytes)", l2Packet.Payload.Length);
        if (SensitiveLogging.Enabled(_logger, _diagnosticLogging))
        {
            _logger.LogTrace("L2 payload: {Data}", SensitiveLogging.BytesToHex(l2Packet.Payload));
        }

        if (l2Packet.Channel == L2Channel.Pb)
        {
            _logger.LogDebug("Processing protobuf packet");
            await ProcessProtobufPacketAsync(l2Packet.Payload);
        }
        else
        {
            _logger.LogDebug("Ignoring non-protobuf channel: {Channel}", l2Packet.Channel);
        }
    }

    private async Task ProcessProtobufPacketAsync(byte[] data)
    {
        try
        {
            _logger.LogDebug("Parsing WearPacket from {Length} bytes", data.Length);
            var packet = WearPacket.Parser.ParseFrom(data);

            _logger.LogInformation("WearPacket: Type={Type}, Id={Id}", packet.Type, packet.Id);

            // Handle authentication packets first
            if (packet.Type == WearPacket.Types.Type.Account)
            {
                _logger.LogDebug("Processing Account packet");
                await _authHandler.ProcessAuthPacketAsync(packet);
            }
            
            // Notify registered handlers
            Action<WearPacket>[] handlers;
            lock (_handlerLock)
            {
                handlers = _packetHandlers.ToArray();
            }
            foreach (var h in handlers)
            {
                try { h(packet); }
                catch (Exception ex) { _logger.LogError(ex, "Packet handler threw"); }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to decode protobuf packet (length={Length})", data?.Length ?? 0);
            if (SensitiveLogging.Enabled(_logger, _diagnosticLogging))
            {
                _logger.LogTrace("Failed protobuf bytes: {Data}", SensitiveLogging.BytesToHex(data));
            }
        }
    }
}
