using YamlDotNet.Core;
using YamlDotNet.Serialization;

namespace Miaosoft.Sim.Serialization.Yaml;

public sealed class ReadOnlyListYamlTypeConverter : IYamlTypeConverter
{
    public bool Accepts(Type type) =>
        type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IReadOnlyList<>);

    public object? ReadYaml(IParser parser, Type type, ObjectDeserializer rootDeserializer) =>
        rootDeserializer(typeof(List<>).MakeGenericType(type.GetGenericArguments()));

    public void WriteYaml(IEmitter emitter, object? value, Type type, ObjectSerializer serializer) =>
        throw new NotSupportedException("This converter is used only for deserialization.");
}
