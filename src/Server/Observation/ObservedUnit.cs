using Miaosoft.Sim.Combat.Map;
using Miaosoft.Sim.Combat.Skill;

namespace Miaosoft.Sim.Server.Observation;

public sealed record ObservedUnit(Unit Unit, MapObject Representation);
