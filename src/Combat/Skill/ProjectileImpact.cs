namespace Miaosoft.Sim.Combat.Skill;

public enum ProjectileHitResponse { Ignore, Stop, Pierce }

[Flags]
public enum ProjectileExplosionTrigger { None = 0, OnHit = 1, OnArrival = 2 }
