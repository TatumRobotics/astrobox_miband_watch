using System;
using Microsoft.Extensions.Logging;

namespace XiaomiAstroBoxCSharp.Protocol;

internal static class SensitiveLogging
{
    public static bool Enabled(ILogger logger, bool diagnosticMode)
    {
        return diagnosticMode && logger != null && logger.IsEnabled(LogLevel.Trace);
    }

    public static string BytesToHex(byte[] data)
    {
        return data == null ? "<null>" : BitConverter.ToString(data);
    }

    public static string RedactHex(byte[] data, int keepPrefixBytes = 4)
    {
        if (data == null) return "<null>";
        if (data.Length == 0) return "<empty>";
        keepPrefixBytes = Math.Max(0, Math.Min(keepPrefixBytes, data.Length));
        if (keepPrefixBytes == data.Length) return BytesToHex(data);
        var prefix = new byte[keepPrefixBytes];
        Array.Copy(data, 0, prefix, 0, keepPrefixBytes);
        return $"{BytesToHex(prefix)}-...({data.Length} bytes)";
    }
}

