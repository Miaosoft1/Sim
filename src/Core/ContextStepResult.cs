namespace Miaosoft.Sim.Core;

public readonly record struct ContextStepResult(bool KeepRunning, IEvent? Event);
