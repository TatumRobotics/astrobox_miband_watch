using Microsoft.Extensions.Logging;
using System.Collections.Generic;
using System.IO;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;
using System;

namespace XiaomiAstroBoxCSharp;

public class VibrationSegment
{
    public bool On { get; set; }
    public int Duration { get; set; }
    public int Strength { get; set; }
}

public class DeviceConfig
{
    public string Name { get; set; } = string.Empty;

    [YamlMember(Alias = "mac_address")]
    public string MacAddress { get; set; } = string.Empty;

    [YamlMember(Alias = "auth_key")]
    public string AuthKey { get; set; } = string.Empty;
}

public class LoggingConfig
{
    public string Default { get; set; } = "Information";
    public Dictionary<string, string> Filters { get; set; } = new();

    public static LogLevel ParseLoggingLevel(string level)
    {
        if (Enum.TryParse<LogLevel>(level, true, out var logLevel))
        {
            return logLevel;
        }
        return LogLevel.Information;
    }
}

public class Config
{
    public DeviceConfig Device { get; set; } = new();
    public LoggingConfig Logging { get; set; } = new();

    public Dictionary<string, List<VibrationSegment>> Patterns { get; set; } = new();

    public static Config Load(string path)
    {
        var yaml = File.ReadAllText(path);
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .Build();

        return deserializer.Deserialize<Config>(yaml);
    }
}
