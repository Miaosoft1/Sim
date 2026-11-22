using System.Collections.Immutable;
using Miaosoft.Sim.Content.Core;
using Miaosoft.Sim.Combat.Map.Content.Dto;
using Miaosoft.Sim.Combat.Map.Content.Parser;
using Miaosoft.Sim.Combat.Map.Content.Validation;
using Miaosoft.Sim.Combat.Map;

namespace Miaosoft.Sim.Combat.Map.Content.Registration;

internal sealed class MapContentModule : IContentModule
{
    private readonly MapParser parser;

    public MapContentModule(MapParser parser) => this.parser = parser;

    public string Id => "map";

    public void Register(ContentSectionRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        var maps = new Dictionary<string, MapDto>(StringComparer.Ordinal);
        var sources = new Dictionary<object, string>(ReferenceEqualityComparer.Instance);
        registry.RegisterReferenceResolver("map", id =>
            registry.Get<IReadOnlyDictionary<string, MapDefinition>>("map").ContainsKey(id));
        registry.RegisterCollection<IReadOnlyDictionary<string, MapDefinition>>("map", (yaml, context) =>
        {
            List<MapDto> entries = context.Deserialize<List<MapDto>>(yaml)
                ?? throw new InvalidDataException($"{context.SourceFile}: map section must be a list.");
            foreach (MapDto dto in entries)
            {
                if (dto == null || string.IsNullOrWhiteSpace(dto.Id))
                    throw new InvalidDataException($"{context.SourceFile}: map requires an ID.");
                if (!maps.TryAdd(dto.Id, dto))
                    throw new InvalidDataException($"{context.SourceFile}: duplicate map '{dto.Id}'.");
                sources[dto] = context.SourceFile;
            }
        }, () =>
        {
            var validation = new ContentValidationContext(sources);
            MapContentValidator.Validate(maps, validation);
            var result = ImmutableDictionary.CreateBuilder<string, MapDefinition>(StringComparer.Ordinal);
            foreach ((string id, MapDto dto) in maps)
            {
                try { result.Add(id, parser.Parse(dto)); }
                catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
                {
                    throw new InvalidDataException($"{validation.Source(dto)}: map '{id}': {exception.Message}", exception);
                }
            }
            return result.ToImmutable();
        });
    }
}
