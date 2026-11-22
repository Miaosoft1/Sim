using System.Globalization;
using Microsoft.AspNetCore.SignalR;
using Miaosoft.Sim.Client;
using Miaosoft.Sim.Protocol.Command;
using Miaosoft.Sim.Protocol.Model;

if (args.Contains("--help", StringComparer.Ordinal))
{
    Console.WriteLine("Miaosoft.Sim console: [--endpoint http://127.0.0.1:5050/session]");
    Console.WriteLine("Commands: status | cast <ability-id> <target-name-or-id> | move <x> <y> | stop | retry | clear | disconnect | rejoin | exit");
    return 0;
}
string address = "http://127.0.0.1:5050/session";
if (args.Length != 0)
{
    if (args.Length != 2 || args[0] != "--endpoint")
    {
        Console.Error.WriteLine("Use --help or --endpoint <session-url>.");
        return 2;
    }
    address = args[1];
}
if (!Uri.TryCreate(address, UriKind.Absolute, out Uri? endpoint))
{
    Console.Error.WriteLine("The session endpoint is invalid.");
    return 2;
}
using var stopping = new CancellationTokenSource();
Console.CancelKeyPress += (_, ev) => { ev.Cancel = true; stopping.Cancel(); };
await using var client = new GameClient();
client.ConnectionLost += error => Console.Error.WriteLine(
    $"Connection lost: {error?.Message ?? "closed"}. Retain the same participant; use rejoin.");
client.ObservationRejected += error => Console.Error.WriteLine($"Observation rejected: {error.Message}");
try
{
    await client.EnterDemoAsync(endpoint, stopping.Token);
    Console.WriteLine("Connected. Start a second console for the other participant. Type status or --help commands; credentials stay in memory.");
    Show(client);
    while (!stopping.IsCancellationRequested)
    {
        Console.Write("> ");
        string? line = await Console.In.ReadLineAsync(stopping.Token);
        if (line == null || line.Equals("exit", StringComparison.OrdinalIgnoreCase)) break;
        string[] words = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length == 0) continue;
        try
        {
            if (words[0] == "status") { Show(client); continue; }
            if (words[0] == "disconnect") { await client.DisconnectAsync(stopping.Token); continue; }
            if (words[0] == "rejoin") { await client.RejoinAsync(cancellationToken: stopping.Token); Show(client); continue; }
            if (words[0] == "clear") { await client.ClearUnqueuedPendingAsync(stopping.Token); continue; }
            OperationReceipt receipt;
            if (words[0] == "retry")
                receipt = await client.RetryPendingAsync(stopping.Token);
            else
            {
                UnitSnapshot? self = client.World.State.World.Units.SingleOrDefault(
                    unit => unit.Id == client.World.State.Binding.UnitId);
                if (self?.MapInstanceId is not { } map)
                {
                    Console.Error.WriteLine("No controlled representation is available.");
                    continue;
                }
                if (words[0] == "stop" && words.Length == 1)
                    receipt = await client.SendAsync(new StopMoveCommand
                        { UnitId = self.Id, MapInstanceId = map, ControlVersion = self.ControlVersion }, stopping.Token);
                else if (words[0] == "move" && words.Length == 3 &&
                    double.TryParse(words[1], CultureInfo.InvariantCulture, out double x) &&
                    double.TryParse(words[2], CultureInfo.InvariantCulture, out double y) && double.IsFinite(x) && double.IsFinite(y))
                    receipt = await client.SendAsync(new MoveCommand
                    {
                        UnitId = self.Id, MapInstanceId = map, ControlVersion = self.ControlVersion, Destination = new(x, y)
                    }, stopping.Token);
                else if (words[0] == "cast" && words.Length == 3)
                {
                    AbilitySnapshot[] abilities = self.Abilities.Where(ability =>
                        ability.DefinitionId.Equals(words[1], StringComparison.Ordinal) ||
                        ability.RuntimeInstanceId.ToString().Equals(words[1], StringComparison.OrdinalIgnoreCase)).ToArray();
                    UnitSnapshot[] targets = client.World.State.World.Units.Where(unit =>
                        unit.Name.Equals(words[2], StringComparison.OrdinalIgnoreCase) ||
                        unit.Id.ToString().Equals(words[2], StringComparison.OrdinalIgnoreCase)).ToArray();
                    if (abilities.Length != 1 || targets.Length != 1)
                    {
                        Console.Error.WriteLine("Choose one observed target and one actual ability instance; use runtime IDs if names are ambiguous.");
                        continue;
                    }
                    receipt = await client.SendAsync(new CastAbilityCommand
                    {
                        UnitId = self.Id, MapInstanceId = map, ControlVersion = self.ControlVersion,
                        AbilityInstanceId = abilities[0].RuntimeInstanceId, TargetUnitId = targets[0].Id
                    }, stopping.Token);
                }
                else
                {
                    Console.Error.WriteLine("Commands: status, cast <ability> <target>, move <x> <y>, stop, retry, clear, disconnect, rejoin, exit.");
                    continue;
                }
            }
            Console.WriteLine($"queue {receipt.Sequence}: {receipt.Status} {receipt.Reason}");
            if (receipt.Status != OperationQueueStatus.Enqueued) continue;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stopping.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                OperationResult outcome = await client.WaitForOutcomeAsync(receipt.Sequence, timeout.Token);
                Console.WriteLine($"execution {outcome.OperationSequence}: {outcome.Status} {outcome.Reason}");
                if (outcome.Status == OperationExecutionStatus.Unknown)
                    Console.Error.WriteLine("The original execution cannot be determined. Do not retry under a new number.");
            }
            catch (OperationCanceledException) when (!stopping.IsCancellationRequested)
            {
                Console.Error.WriteLine("Execution outcome is unknown. Queue acceptance is not execution success; do not resend under a new number.");
            }
        }
        catch (HubException error) { Console.Error.WriteLine(error.Message); }
        catch (InvalidOperationException error) { Console.Error.WriteLine(error.Message); }
        catch (InvalidDataException error) { Console.Error.WriteLine(error.Message); }
    }
    return 0;
}
catch (OperationCanceledException) when (stopping.IsCancellationRequested) { return 0; }
catch (HttpRequestException error) { Console.Error.WriteLine(error.Message); return 1; }
catch (HubException error) { Console.Error.WriteLine(error.Message); return 1; }
catch (ArgumentException error) { Console.Error.WriteLine(error.Message); return 2; }

static void Show(GameClient client)
{
    ClientObservationState state = client.World.State;
    Console.WriteLine($"participant {state.Binding.ParticipantId}, connection {state.Binding.ConnectionVersion}, state {state.StateVersion}, scale {state.World.TimeScale}");
    foreach (UnitSnapshot unit in state.World.Units)
    {
        Console.WriteLine($"{unit.Name} {unit.Id}: life {unit.Life}/{unit.MaximumLife}, energy {unit.Energy}/{unit.MaximumEnergy}, position {unit.Position.X:F2},{unit.Position.Y:F2}");
        foreach (AbilitySnapshot ability in unit.Abilities)
            Console.WriteLine($"  {ability.DefinitionId} [{ability.RuntimeInstanceId}], level {ability.Level}, charges {ability.AvailableCharges?.ToString() ?? "unlimited"}");
    }
}
