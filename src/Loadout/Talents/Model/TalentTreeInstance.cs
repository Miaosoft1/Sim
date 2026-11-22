using System.Collections.ObjectModel;
using Miaosoft.Sim.Combat.Skill;
using Miaosoft.Sim.Combat.Skill.Ability;
using Miaosoft.Sim.Core;
using Miaosoft.Sim.Loadout.Talents.Handler;

namespace Miaosoft.Sim.Loadout.Talents.Model;

public sealed record TalentGrowthProgress(int PendingProgress, long Rewards);

public sealed class TalentTreeInstance
{
    internal sealed record Grant(IAbilitySource Source, Guid Id, string Category);
    internal sealed record State(
        Grant[] Grants, long AuthorityVersion, int AvailablePoints,
        IReadOnlyDictionary<string, int> Ranks, IReadOnlyDictionary<string, TalentGrowthProgress> Growth);
    internal AtomicCell<State> Runtime { get; }
    internal TalentTreeHandler Observer { get; }
    internal UnitTalents Directory { get; }

    internal TalentTreeInstance(UnitTalents directory, TalentTreeDefinition definition, Context context)
    {
        Directory = directory;
        Definition = definition;
        Context = context;
        Runtime = new(new([], 0, definition.InitialPoints,
            new ReadOnlyDictionary<string, int>(new Dictionary<string, int>(StringComparer.Ordinal)),
            new ReadOnlyDictionary<string, TalentGrowthProgress>(
                new Dictionary<string, TalentGrowthProgress>(StringComparer.Ordinal))));
        NodeSources = new ReadOnlyDictionary<string, AbilitySourceInstance>(definition.Nodes.Values.ToDictionary(
            node => node.Id, node => new AbilitySourceInstance("Talent", node.Abilities, directory.Owner), StringComparer.Ordinal));
        GrowthSource = new("TalentGrowth", definition.GrowthRules.Select(rule => new AbilitySourceMember(rule.AbilityId)), directory.Owner);
        Observer = new(this);
    }

    public Guid InstanceId { get; } = Guid.NewGuid();
    public TalentTreeDefinition Definition { get; }
    public Unit Owner => Directory.Owner;
    public Context Context { get; }
    public long Revision => Runtime.Version;
    public bool IsAuthorized => Runtime.Value.Grants.Length != 0;
    public long AuthorityVersion => Runtime.Value.AuthorityVersion;
    public int GrantCount => Runtime.Value.Grants.Length;
    public int AvailablePoints => Runtime.Value.AvailablePoints;
    public IReadOnlyDictionary<string, int> Ranks => Runtime.Value.Ranks;
    public IReadOnlyDictionary<string, TalentGrowthProgress> Growth => Runtime.Value.Growth;
    public IReadOnlyDictionary<string, AbilitySourceInstance> NodeSources { get; }
    public AbilitySourceInstance GrowthSource { get; }
}
