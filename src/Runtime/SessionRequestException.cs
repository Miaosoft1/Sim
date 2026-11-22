namespace Miaosoft.Sim.Runtime;

public enum SessionRequestFailure
{
    Unauthorized, UnsupportedProtocol, UnsupportedModule, DemoDisabled,
    SessionFull, Conflict, Unavailable, InvalidRequest
}

public sealed class SessionRequestException(SessionRequestFailure failure, string message) : Exception(message)
{
    public SessionRequestFailure Failure { get; } = failure;
}
