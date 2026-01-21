using System;
using System.Buffers.Binary;
using Microsoft.Extensions.Logging;

namespace XiaomiAstroBoxCSharp.Protocol;

/// <summary>
/// Manages buffering and extraction of L1 packets from raw byte streams
/// </summary>
public class PacketBuffer(ILogger logger)
{

    // Backing buffer using a resizable array for fewer allocations than List<byte>.
    private byte[] _buffer = new byte[512];
    private int _length;

    private const int HeaderSize = 8;
    private const int LengthOffset = 4;
    // Guardrail against unbounded growth if the stream is corrupted or not aligned.
    private const int MaxBufferBytes = 1024 * 1024; // 1 MiB

    /// <summary>
    /// Adds data to the buffer
    /// (preserves original API signature: byte[] parameter and debug logging)
    /// </summary>
    public void AddData(byte[] data)
    {
        if (data == null || data.Length == 0)
        {
            logger.LogDebug("AddData called with empty data");
            return;
        }

        if (_length + data.Length > MaxBufferBytes)
        {
            logger.LogWarning(
                "Packet buffer would exceed max size ({Max} bytes). Clearing buffer to resync.",
                MaxBufferBytes);
            ClearPreservingMagicPrefix();
        }

        EnsureCapacity(data.Length);
        data.AsSpan().CopyTo(_buffer.AsSpan(_length));
        _length += data.Length;

        logger.LogDebug("Buffer now has {Count} bytes", _length);
    }

    /// <summary>
    /// Attempts to extract a complete L1 packet from the buffer
    /// </summary>
    /// <param name="packet">The extracted packet, or null if no complete packet is available</param>
    /// <returns>True if a packet was extracted, false otherwise</returns>
    public bool TryExtractPacket(out L1Packet packet)
    {
        packet = null;

        // Need at least header size
        if (_length < HeaderSize)
        {
            logger.LogTrace("Buffer too small for L1 header: {Count} bytes", _length);
            return false;
        }

        // Find magic bytes (0xA5A5)
        var span = _buffer.AsSpan(0, _length);
        int magicIndex = FindMagic(span);
        if (magicIndex == -1)
        {
            logger.LogWarning("No magic bytes found in buffer, clearing {Count} bytes", _length);
            ClearPreservingMagicPrefix();
            return false;
        }

        // Remove any garbage before magic
        if (magicIndex > 0)
        {
            logger.LogDebug("Removing {Count} garbage bytes before magic", magicIndex);
            Consume(magicIndex);
        }

        // Re-evaluate span/length after alignment
        if (_length < HeaderSize)
        {
            logger.LogTrace("Buffer too small after magic alignment: {Count} bytes", _length);
            return false;
        }

        span = _buffer.AsSpan(0, _length);

        // Read packet length (explicit little-endian)
        ushort payloadLen = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(LengthOffset, 2));
        int totalLength = HeaderSize + payloadLen;
        logger.LogDebug("L1 packet length: {Length}, total: {Total}", payloadLen, totalLength);

        // Check if we have complete packet
        if (_length < totalLength)
        {
            logger.LogDebug("Waiting for more data: have {Have}, need {Need}", _length, totalLength);
            return false;
        }

        // Extract packet data (make a copy for parsing and logging)
        var packetData = new byte[totalLength];
        span.Slice(0, totalLength).CopyTo(packetData);
        Consume(totalLength);

        logger.LogDebug("Extracted L1 packet: {Data}", BitConverter.ToString(packetData));

        // Parse the packet
        packet = L1Packet.FromBytes(packetData);
        if (packet == null)
        {
            logger.LogWarning("Failed to parse L1 packet from extracted data");
        }

        return packet != null;
    }

    /// <summary>
    /// Finds the index of magic bytes (0xA5A5) in the span
    /// </summary>
    private static int FindMagic(ReadOnlySpan<byte> span)
    {
        if (span.Length < 2) return -1;
        for (int i = 0; i <= span.Length - 2; i++)
        {
            if (span[i] == 0xA5 && span[i + 1] == 0xA5)
            {
                return i;
            }
        }
        return -1;
    }

    /// <summary>
    /// Shift-left the buffer by 'count' bytes (consumes them)
    /// </summary>
    private void Consume(int count)
    {
        if (count <= 0) return;
        if (count >= _length)
        {
            _length = 0;
            return;
        }

        _buffer.AsSpan(count, _length - count).CopyTo(_buffer);
        _length -= count;
    }

    /// <summary>
    /// Ensure the backing buffer can accommodate additional bytes, resizing by doubling when needed
    /// </summary>
    private void EnsureCapacity(int additional)
    {
        int required = _length + additional;
        if (required <= _buffer.Length) return;
        int newSize = Math.Max(_buffer.Length * 2, required);
        Array.Resize(ref _buffer, newSize);
    }

    /// <summary>
    /// Gets the current buffer size
    /// (preserves original API)
    /// </summary>
    public int Count => _length;

    /// <summary>
    /// Clears the buffer
    /// (preserves original API)
    /// </summary>
    public void Clear()
    {
        _length = 0;
    }

    /// <summary>
    /// Clears the buffer, but preserves a trailing 0xA5 byte as a potential start-of-magic prefix.
    /// This improves resync when reads split the magic bytes across boundaries.
    /// </summary>
    private void ClearPreservingMagicPrefix()
    {
        if (_length > 0 && _buffer[_length - 1] == 0xA5)
        {
            _buffer[0] = 0xA5;
            _length = 1;
            return;
        }
        _length = 0;
    }
}
