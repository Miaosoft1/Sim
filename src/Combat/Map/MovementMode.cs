namespace Miaosoft.Sim.Combat.Map;

public enum MovementMode { Linear, Homing, Instant }

public enum MovementEndReason { Arrived, Blocked, Stopped, Replaced, Missed, MapChanged }
