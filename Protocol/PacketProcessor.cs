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
    private Action<byte> _onAckReceived;
    private Action<WearPacket> _onPacketReceived;

    public PacketProcessor(
        ILogger<PacketProcessor> logger,
        AuthenticationHandler authHandler,
        Func<byte, Task> sendAckFunc)
    {
        _logger = logger;
        _authHandler = authHandler;
        _sendAckFunc = sendAckFunc;
        _packetBuffer = new PacketBuffer(logger);
    }

    /// <summary>
    /// Register a callback to be invoked when an ACK packet is received
    /// </summary>
    /// <param name="callback">Action that receives the sequence number of the ACK</param>
    public void OnAckReceived(Action<byte> callback)
    {
        _onAckReceived = callback;
    }

    /// <summary>
    /// Register a callback to be invoked when a packet is received
    /// </summary>
    /// <param name="callback">Action that receives the WearPacket</param>
    public void OnPacketReceived(Action<WearPacket> callback)
    {
        _onPacketReceived = callback;
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
        _logger.LogInformation("WATCH DATA RECEIVED: {Count} bytes: {Data}", 
            data.Length, BitConverter.ToString(data));
    }

    private void LogPacketInfo(L1Packet packet, string prefix = "")
    {
        _logger.LogInformation("{Prefix}L1 packet: Type={Type}, Seq={Seq}, Length={Length}, Frx={Frx}", 
            prefix, packet.Type, packet.Seq, packet.Length, packet.Frx);
        _logger.LogDebug("L1 payload ({Length} bytes): {Data}", 
            packet.Payload.Length, BitConverter.ToString(packet.Payload));
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
        _onAckReceived?.Invoke(l1Packet.Seq);
    }

    private void HandleNakPacket(L1Packet l1Packet)
    {
        _logger.LogWarning("Received NAK for seq {Seq}", l1Packet.Seq);
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
        _logger.LogDebug("L2 payload: {Data}", BitConverter.ToString(l2Packet.Payload));

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
            _onPacketReceived?.Invoke(packet);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to decode protobuf packet. Data: {Data}", BitConverter.ToString(data));
        }
    }
}
