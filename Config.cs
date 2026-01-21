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

    /// <summary>
    /// Enables sensitive/raw logging when the logger is set to Trace.
    /// Off by default to avoid leaking secrets (auth material, encrypted payloads, raw packet bytes).
    /// </summary>
    [YamlMember(Alias = "diagnostic_mode")]
    public bool DiagnosticMode { get; set; } = false;

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

    public BatteryMonitoringConfig BatteryMonitoring { get; set; } = new();

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

public class BatteryMonitoringConfig
{
    public bool Enabled { get; set; } = true;

    [YamlMember(Alias = "low_battery_threshold")]
    public uint LowBatteryThreshold { get; set; } = 20;

    // Fixed by requirement but left configurable for future tuning.
    [YamlMember(Alias = "check_interval_hours")]
    public int CheckIntervalHours { get; set; } = 6;

    [YamlMember(Alias = "notify_interval_minutes")]
    public int NotifyIntervalMinutes { get; set; } = 30;

    // How often to poll for charging state while in low-battery notify mode.
    [YamlMember(Alias = "charging_poll_seconds")]
    public int ChargingPollSeconds { get; set; } = 60;

    // Name of a pattern in `patterns:` to play for low battery notifications.
    [YamlMember(Alias = "low_battery_pattern")]
    public string LowBatteryPattern { get; set; } = "sos";
}
