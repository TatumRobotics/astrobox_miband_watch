using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;

namespace XiaomiAstroBoxCSharp.Protocol;

/// <summary>
/// Manages buffering and extraction of L1 packets from raw byte streams
/// </summary>
public class PacketBuffer
{
    private readonly ILogger _logger;
    private readonly List<byte> _buffer = [];

    public PacketBuffer(ILogger logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Adds data to the buffer
    /// </summary>
    public void AddData(byte[] data)
    {
        _buffer.AddRange(data);
        _logger.LogDebug("Buffer now has {Count} bytes", _buffer.Count);
    }

    /// <summary>
    /// Attempts to extract a complete L1 packet from the buffer
    /// </summary>
    /// <param name="packet">The extracted packet, or null if no complete packet is available</param>
    /// <returns>True if a packet was extracted, false otherwise</returns>
    public bool TryExtractPacket(out L1Packet packet)
    {
        packet = null;

        // Need at least 8 bytes for header
        if (_buffer.Count < 8)
        {
            _logger.LogTrace("Buffer too small for L1 header: {Count} bytes", _buffer.Count);
            return false;
        }

        // Find magic bytes (0xA5A5)
        var magicIndex = FindMagicBytes();
        if (magicIndex == -1)
        {
            _logger.LogWarning("No magic bytes found in buffer, clearing {Count} bytes", _buffer.Count);
            _buffer.Clear();
            return false;
        }

        // Remove any garbage before magic
        if (magicIndex > 0)
        {
            _logger.LogDebug("Removing {Count} garbage bytes before magic", magicIndex);
            _buffer.RemoveRange(0, magicIndex);
        }

        // Check if we have length field
        if (_buffer.Count < 8)
        {
            _logger.LogTrace("Buffer too small after magic alignment: {Count} bytes", _buffer.Count);
            return false;
        }

        // Read packet length and calculate total size
        var length = BitConverter.ToUInt16(_buffer.ToArray(), 4);
        var totalLength = 8 + length;
        _logger.LogDebug("L1 packet length: {Length}, total: {Total}", length, totalLength);

        // Check if we have complete packet
        if (_buffer.Count < totalLength)
        {
            _logger.LogDebug("Waiting for more data: have {Have}, need {Need}", _buffer.Count, totalLength);
            return false;
        }

        // Extract packet data
        var packetData = _buffer.Take(totalLength).ToArray();
        _buffer.RemoveRange(0, totalLength);
        _logger.LogDebug("Extracted L1 packet: {Data}", BitConverter.ToString(packetData));

        // Parse the packet
        packet = L1Packet.FromBytes(packetData);
        if (packet == null)
        {
            _logger.LogWarning("Failed to parse L1 packet from extracted data");
        }
        
        return packet != null;
    }

    /// <summary>
    /// Finds the index of magic bytes (0xA5A5) in the buffer
    /// </summary>
    private int FindMagicBytes()
    {
        for (int i = 0; i <= _buffer.Count - 2; i++)
        {
            if (_buffer[i] == 0xA5 && _buffer[i + 1] == 0xA5)
            {
                return i;
            }
        }
        return -1;
    }

    /// <summary>
    /// Gets the current buffer size
    /// </summary>
    public int Count => _buffer.Count;

    /// <summary>
    /// Clears the buffer
    /// </summary>
    public void Clear()
    {
        _buffer.Clear();
    }
}
