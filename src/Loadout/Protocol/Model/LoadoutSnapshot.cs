namespace Miaosoft.Sim.Loadout.Protocol.Model;

public sealed record ItemSnapshot
{
    public Guid InstanceId { get; init; }
    public string DefinitionId { get; init; } = "";
    public string Name { get; init; } = "";
    public string? SlotId { get; init; }
    public IReadOnlyList<Guid> AbilityInstanceIds { get; init; } = [];
}

public sealed record InventorySnapshot
{
    public long Revision { get; init; }
    public IReadOnlyList<ItemSnapshot> Items { get; init; } = [];
}

public sealed record TalentRankSnapshot(string NodeId, int Rank)
{
    public TalentRankSnapshot() : this("", 0) { }
}

public sealed record TalentProgressSnapshot(string RuleId, int PendingProgress, long Rewards)
{
    public TalentProgressSnapshot() : this("", 0, 0) { }
}

public sealed record TalentSnapshot
{
    public string TreeId { get; init; } = "";
    public Guid InstanceId { get; init; }
    public long Revision { get; init; }
    public long AuthorityVersion { get; init; }
    public bool IsAuthorized { get; init; }
    public int AvailablePoints { get; init; }
    public IReadOnlyList<TalentRankSnapshot> Ranks { get; init; } = [];
    public IReadOnlyList<TalentProgressSnapshot> Growth { get; init; } = [];
}

public sealed record ExchangeSnapshot
{
    public Guid InstanceId { get; init; }
    public Guid OtherUnitId { get; init; }
    public long TermsRevision { get; init; }
    public bool IsClosed { get; init; }
    public bool SelfConfirmed { get; init; }
    public bool OtherConfirmed { get; init; }
    public bool InventoriesCurrent { get; init; }
    public IReadOnlyList<Command.ItemOffer> SelfOffer { get; init; } = [];
    public IReadOnlyList<Command.ItemOffer> OtherOffer { get; init; } = [];
    public IReadOnlyList<ItemSnapshot> OtherItems { get; init; } = [];
}

public sealed record DeathInspectionSnapshot
{
    public Guid AuthorizationId { get; init; }
    public Guid OwnerUnitId { get; init; }
    public Guid DeathId { get; init; }
    public long Revision { get; init; }
    public string Access { get; init; } = "";
    public IReadOnlyList<ItemSnapshot> Items { get; init; } = [];
}

public sealed record LoadoutSnapshot
{
    public Guid UnitId { get; init; }
    public InventorySnapshot? Inventory { get; init; }
    public IReadOnlyList<TalentSnapshot> Talents { get; init; } = [];
    public IReadOnlyList<ExchangeSnapshot> Exchanges { get; init; } = [];
    public IReadOnlyList<DeathInspectionSnapshot> DeathInspections { get; init; } = [];
}
