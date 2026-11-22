using Miaosoft.Sim.Protocol.Command;
using Miaosoft.Sim.Protocol.Model;

namespace Miaosoft.Sim.Protocol.Event;

public static class EventCodec
{
    public static EventCodecBuilder CreateBuilder() => new EventCodecBuilder()
        .Register<MoveCommand>("move", EventDirection.ClientToServer)
        .Register<StopMoveCommand>("move.stop", EventDirection.ClientToServer)
        .Register<CastAbilityCommand>("ability.cast", EventDirection.ClientToServer)
        .Register<OperationResult>("operation.result", EventDirection.ServerToClient)
        .Register<ProjectileFact>("projectile.fact", EventDirection.ServerToClient);
}
