using System.Collections.Generic;
using System.IO;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

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

public class Config
{
    public DeviceConfig Device { get; set; } = new();

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
