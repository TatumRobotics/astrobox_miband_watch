using System;
using System.Collections.Generic;

namespace XiaomiAstroBoxCSharp.Protocol;

public enum L1DataType : byte
{
    Nak = 0,
    Ack = 1,
    Cmd = 2,
    Data = 3,
}

public enum L2Channel : byte
{
    Pb = 1,
    Mass = 2,
    MassVoice = 3,
    FileSensor = 4,
    FileFitness = 5,
    Ota = 6,
    Network = 7,
    Lyra = 8,
    Research = 9,
}

public enum L2OpCode : byte
{
    Write = 1,
    WriteEnc = 2,
    Read = 3,
}

public enum CmdCode : byte
{
    CmdL1StartReq = 1,
    CmdL1StartRsp = 2,
    CmdL1StopReq = 3,
    CmdL1StopRsp = 4,
}

public class L1Packet
{
    public const ushort MAGIC = 0xA5A5;
    private const byte TYPE_MASK = 0x0F;
    private const byte FRX_MASK = 0x10;

    public L1DataType Type { get; set; }
    public bool Frx { get; set; }
    public byte Seq { get; set; }
    public ushort Length { get; set; }
    public ushort Crc { get; set; }
    public byte[] Payload { get; set; }

    public L1Packet(L1DataType type, bool frx, byte seq, byte[] payload)
    {
        Type = type;
        Frx = frx;
        Seq = seq;
        Payload = payload;
        Length = (ushort)payload.Length;
        UpdateCrc();
    }

    public byte[] ToBytes()
    {
        var output = new List<byte>();

        // Magic
        output.AddRange(BitConverter.GetBytes(MAGIC));

        // Type | Frx
        output.Add(PackTypeFrx(Type, Frx));

        // Seq
        output.Add(Seq);

        // Length
        output.AddRange(BitConverter.GetBytes(Length));

        // CRC
        output.AddRange(BitConverter.GetBytes(Crc));

        // Payload
        output.AddRange(Payload);

        return output.ToArray();
    }

    public static L1Packet FromBytes(byte[] buffer)
    {
        if (buffer.Length < 8)
            return null;

        var magic = BitConverter.ToUInt16(buffer, 0);
        if (magic != MAGIC)
            return null;

        var (type, frx) = UnpackTypeFrx(buffer[2]);
        var seq = buffer[3];
        var length = BitConverter.ToUInt16(buffer, 4);
        var declaredCrc = BitConverter.ToUInt16(buffer, 6);

        if (buffer.Length < 8 + length)
            return null;

        var payload = new byte[length];
        Array.Copy(buffer, 8, payload, 0, length);

        var computedCrc = Crc16Arc(payload);
        if (declaredCrc != computedCrc)
        {
            // CRC mismatch - packet corrupted
            return null;
        }

        return new L1Packet(type, frx, seq, payload)
        {
            Length = length,
            Crc = declaredCrc
        };
    }

    private void UpdateCrc()
    {
        Length = (ushort)Payload.Length;
        Crc = Crc16Arc(Payload);
    }

    public static ushort Crc16Arc(byte[] data)
    {
        ushort crc = 0x0000;
        foreach (var b in data)
        {
            crc ^= b;
            for (int i = 0; i < 8; i++)
            {
                if ((crc & 0x0001) != 0)
                    crc = (ushort)(crc >> 1 ^ 0xA001);
                else
                    crc >>= 1;
            }
        }
        return crc;
    }

    private static byte PackTypeFrx(L1DataType type, bool frx)
    {
        byte b = (byte)((byte)type & TYPE_MASK);
        if (frx)
            b |= FRX_MASK;
        return b;
    }

    private static (L1DataType type, bool frx) UnpackTypeFrx(byte b)
    {
        var type = (L1DataType)(b & TYPE_MASK);
        var frx = (b & FRX_MASK) != 0;
        return (type, frx);
    }
}

public class L2Packet
{
    public L2Channel Channel { get; set; }
    public L2OpCode OpCode { get; set; }
    public byte[] Payload { get; set; }

    public L2Packet(L2Channel channel, L2OpCode opCode, byte[] payload)
    {
        Channel = channel;
        OpCode = opCode;
        Payload = payload;
    }

    public byte[] ToBytes()
    {
        var output = new List<byte>
        {
            (byte)Channel,
            (byte)OpCode
        };
        output.AddRange(Payload);
        return output.ToArray();
    }

    public static L2Packet FromBytes(byte[] buffer, IL2Cipher cipher = null)
    {
        if (buffer.Length < 2)
            return null;

        var channel = (L2Channel)buffer[0];
        var opCode = (L2OpCode)buffer[1];
        var body = buffer[2..];

        byte[] payload;
        if (opCode == L2OpCode.WriteEnc && cipher != null)
        {
            payload = cipher.Decrypt(body);
        }
        else
        {
            payload = body;
        }

        return new L2Packet(channel, opCode, payload);
    }

    public L1Packet ToL1(byte seq, bool frx = false)
    {
        return new L1Packet(L1DataType.Data, frx, seq, ToBytes());
    }

    public static L2Packet FromL1(L1Packet l1, IL2Cipher cipher = null)
    {
        if (l1.Type != L1DataType.Data)
            return null;

        return FromBytes(l1.Payload, cipher);
    }
}

public class L1CmdPacket
{
    public CmdCode Cmd { get; set; }
    public Dictionary<byte, byte[]> Config { get; set; } = new();
    
    // Config type constants
    public const byte CONFIG_TYPE_VERSION = 0x01;
    public const byte CONFIG_TYPE_MPS = 0x02;
    public const byte CONFIG_TYPE_TX_WIN = 0x03;
    public const byte CONFIG_TYPE_SEND_TIMEOUT = 0x04;
    public const byte CONFIG_TYPE_DEVICE_TYPE = 0x05;
    
    public L1CmdPacket(CmdCode cmd)
    {
        Cmd = cmd;
    }
    
    public byte[] ToBytes()
    {
        var payload = new List<byte> { (byte)Cmd };
        
        foreach (var (key, value) in Config)
        {
            payload.Add(key);
            payload.AddRange(BitConverter.GetBytes((ushort)value.Length));
            payload.AddRange(value);
        }
        
        return payload.ToArray();
    }
    
    public static L1CmdPacket FromBytes(byte[] buffer)
    {
        if (buffer.Length < 1)
            return null;
        
        var cmd = (CmdCode)buffer[0];
        var packet = new L1CmdPacket(cmd);
        
        int pos = 1;
        while (pos + 3 <= buffer.Length)
        {
            byte configType = buffer[pos];
            ushort configLen = BitConverter.ToUInt16(buffer, pos + 1);
            pos += 3;
            
            if (pos + configLen > buffer.Length)
                break;
            
            byte[] configValue = new byte[configLen];
            Array.Copy(buffer, pos, configValue, 0, configLen);
            pos += configLen;
            
            packet.Config[configType] = configValue;
        }
        
        return packet;
    }
}

public interface IL2Cipher
{
    byte[] Encrypt(byte[] plaintext);
    byte[] Decrypt(byte[] ciphertext);
}
