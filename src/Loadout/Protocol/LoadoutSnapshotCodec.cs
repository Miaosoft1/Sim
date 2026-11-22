using Miaosoft.Sim.Loadout.Protocol.Model;
using Miaosoft.Sim.Serialization.Yaml;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Miaosoft.Sim.Loadout.Protocol;

public sealed class LoadoutSnapshotCodec
{
    public const string ModuleId = "loadout";
    public const int ModuleVersion = 1;
    private readonly object sync = new();
    private readonly ISerializer serializer = new SerializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .EnsureRoundtrip().DisableAliases().Build();
    private readonly IDeserializer deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance).WithDuplicateKeyChecking()
        .WithTypeConverter(new ReadOnlyListYamlTypeConverter()).Build();

    public string Write(LoadoutSnapshot state)
    {
        ArgumentNullException.ThrowIfNull(state);
        lock (sync) return serializer.Serialize(state);
    }

    public LoadoutSnapshot Read(string payload)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(payload);
        lock (sync) return deserializer.Deserialize<LoadoutSnapshot>(payload) ??
            throw new InvalidDataException("A Loadout snapshot is required.");
    }
}
