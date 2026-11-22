namespace Miaosoft.Sim.Combat.Skill.Ability;

public sealed record AbilitySourceMember(string AbilityId, int Level = 1);

public sealed class AbilitySourceInstance : IAbilitySource
{
    internal sealed record Group(AbilityBase Ability, IReadOnlyList<IHandler> Parts);
    internal sealed record State(Context? Context, Unit? Holder, IReadOnlyList<Group>? Groups);
    internal AtomicCell<State> Runtime { get; } = new(new(null, null, null));

    public AbilitySourceInstance(string category, IEnumerable<AbilitySourceMember> members, Unit? fixedOwner = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(category);
        ArgumentNullException.ThrowIfNull(members);
        if (category != category.Trim())
            throw new ArgumentException("Source categories must be normalized.", nameof(category));
        AbilitySourceMember[] definitions = members.ToArray();
        if (definitions.Any(member => member == null || string.IsNullOrWhiteSpace(member.AbilityId) ||
            member.AbilityId != member.AbilityId.Trim() || member.Level < 1))
            throw new ArgumentException("Every source member requires an ability ID and positive level.", nameof(members));
        Category = category;
        Members = Array.AsReadOnly(definitions);
        FixedOwner = fixedOwner;
    }

    public Guid InstanceId { get; } = Guid.NewGuid();
    public long Revision => Runtime.Version;
    public string Category { get; }
    public Unit? FixedOwner { get; }
    public IReadOnlyList<AbilitySourceMember> Members { get; }
    public bool IsMaterialized => Runtime.Value.Groups != null;
    public Context? BoundContext => Runtime.Value.Context;
    public Unit? Holder => Runtime.Value.Holder;
    public IReadOnlyList<AbilityBase> Abilities =>
        Array.AsReadOnly((Runtime.Value.Groups ?? []).Select(group => group.Ability).ToArray());
}
