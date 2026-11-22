namespace Miaosoft.Sim.Combat.Skill.Ability;

public interface IAbilityRuntimePart : IHandler
{
    AbilitySourceBinding Binding { get; }
}

public sealed class AbilitySourceBinding
{
    private sealed record Authority(Context? Context, Guid? OwnerId, long Version);
    private sealed record Grant(IAbilitySource Source, Guid InstanceId, string Category);
    private readonly AtomicCell<Authority> authority = new(new Authority(null, null, 0));
    private readonly AtomicCell<Grant?> source = new(null);
    public Guid InstanceId { get; } = Guid.NewGuid();
    public IAbilitySource? Source => ValidateGrant(source.Value)?.Source;
    public Guid? SourceInstanceId => ValidateGrant(source.Value)?.InstanceId;
    public string? SourceCategory => ValidateGrant(source.Value)?.Category;
    public Guid? OwnerId => authority.Value.OwnerId;
    public long AuthorityVersion => authority.Value.Version;
    public Context? BoundContext => authority.Value.Context;
    public bool IsAuthorized(Guid ownerId) => authority.Value.OwnerId == ownerId;

    public void StageBind(AtomicTransaction transaction, IAbilitySource abilitySource)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(abilitySource);
        Guid instanceId = abilitySource.InstanceId;
        string category = abilitySource.Category;
        if (instanceId == Guid.Empty)
            throw new ArgumentOutOfRangeException(nameof(abilitySource), "A source requires a nonempty instance ID.");
        if (string.IsNullOrWhiteSpace(category) ||
            !string.Equals(category, category.Trim(), StringComparison.Ordinal))
            throw new ArgumentException("A source requires a nonempty, normalized category.", nameof(abilitySource));
        Grant? current = transaction.Read(source);
        if (current != null && !ReferenceEquals(current.Source, abilitySource))
            throw new InvalidOperationException("The ability part already belongs to another source.");
        if (current != null &&
            (current.InstanceId != instanceId || current.Category != category))
            throw new InvalidOperationException("The bound ability source changed its identity or category.");
        if (transaction.Read(authority).Context is { } context)
            AbilitySourceRegistry.Stage(transaction, context, abilitySource, instanceId, category);
        if (current == null)
            transaction.Stage(source, new Grant(abilitySource, instanceId, category));
    }

    public void StageAuthority(AtomicTransaction transaction, Context context, Guid? ownerId)
    {
        Grant grant = transaction.Read(source) ??
            throw new InvalidOperationException("The ability is not bound to a source.");
        ValidateGrant(grant);
        ArgumentNullException.ThrowIfNull(context);
        Authority current = transaction.Read(authority);
        if (current.Context != null && !ReferenceEquals(current.Context, context))
            throw new InvalidOperationException("The ability belongs to another context.");
        AbilitySourceRegistry.Stage(transaction, context, grant.Source, grant.InstanceId, grant.Category);
        if (current.Context == null || current.OwnerId != ownerId)
            transaction.Stage(authority, new Authority(context, ownerId, checked(current.Version + 1)));
    }

    public void StageContext(AtomicTransaction transaction, Context context)
    {
        ArgumentNullException.ThrowIfNull(context);
        Grant? grant = ValidateGrant(transaction.Read(source));
        Authority current = transaction.Read(authority);
        if (current.Context != null && !ReferenceEquals(current.Context, context))
            throw new InvalidOperationException("The ability belongs to another context.");
        if (grant != null)
            AbilitySourceRegistry.Stage(transaction, context, grant.Source, grant.InstanceId, grant.Category);
        if (current.Context == null)
            transaction.Stage(authority, current with { Context = context });
    }

    private static Grant? ValidateGrant(Grant? grant)
    {
        if (grant != null &&
            (grant.Source.InstanceId != grant.InstanceId || grant.Source.Category != grant.Category))
            throw new InvalidOperationException("The bound ability source changed its identity or category.");
        return grant;
    }
}
