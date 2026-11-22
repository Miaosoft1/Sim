using System.Collections;
using Miaosoft.Sim.Core;
namespace Miaosoft.Sim.Core
{
    public enum LogLevel
    {
        Default = 0,
        Normal,
        Detail,
        Debug
    }

    public enum LogSource
    {
        None,
        Handler,
        Event
    }

    public enum GameLogType
    {
        None,
        Name,
        Action,
        Description,
        AttackDamageValue,
        MagicDamageValue,
        TrueDamageValue,
        AttackDamageDescription,
        MagicDamageDescription,
        TrueDamageDescription,
    }

    public record LogPair(GameLogType Type, string Content)
    {
        public override string ToString()
        {
            return this.Content;
        }
    }

    public record Logger(Context Context) : IEnumerable<LogLine>
    {
        public LogLevel LogLevel { get; set; }
        public List<LogLine> Log { get; } = new List<LogLine>();
        public TextWriter? Output { get; set; } = System.Console.Out;

        public LogLine NewLine(LogSource source, LogLevel logLevel = LogLevel.Default)
        {
            LogLine line = new LogLine(Context.ContextTimeSpan, logLevel, source);
            Log.Add(line);
            return line;
        }

        public void RecordQueue(IEvent ev)
        {
            WriteLine(
                NewLine(LogSource.Event, LogLevel.Normal)
                    .Append(GameLogType.Action, "Queued")
                    .Append(GameLogType.Description, ev));
        }

        public void RecordQueueFailed(IEvent ev, string reason)
        {
            WriteLine(
                NewLine(LogSource.Event, LogLevel.Normal)
                    .Append(GameLogType.Action, "QueueFailed")
                    .Append(GameLogType.Description, $"{reason}: {ev}"));
        }

        public void RecordInboxRequestRejected(string request)
        {
            WriteLine(
                NewLine(LogSource.Event, LogLevel.Normal)
                    .Append(GameLogType.Action, "QueueFailed")
                    .Append(GameLogType.Description, $"The context inbox no longer accepts {request} requests."));
        }

        public void RecordProcess(IEvent ev)
        {
            WriteLine(
                NewLine(LogSource.Event, LogLevel.Normal)
                    .Append(GameLogType.Action, "Processed")
                    .Append(GameLogType.Description, ev));
        }

        private void WriteLine(LogLine line)
        {
            if (Output == null)
            {
                return;
            }

            Output.WriteLine(line.ToString());
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return this.Log.GetEnumerator();
        }
        public IEnumerator<LogLine> GetEnumerator()
        {
            return Log.GetEnumerator();
        }
    }

    public record LogLine(TimeSpan TimeSpan, LogLevel LogLevel, LogSource LogSource) : IEnumerable<LogPair>
    {
        private List<LogPair> line = new List<LogPair>();
        public LogLine Append(LogPair gameLog)
        {
            line.Add(gameLog);
            return this;
        }

        public LogLine Append(GameLogType Type, object? Content)
        {
            return this.Append(new LogPair(Type, Content?.ToString() ?? string.Empty));
        }

        public IEnumerator<LogPair> GetEnumerator()
        {
            return line.GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }

        public override string ToString()
        {
            string content = string.Join(" | ", line.Select(m => m.ToString()));
            return $"[{TimeSpan:hh\\:mm\\:ss\\.fff}] {LogSource} {content}";
        }
    }
}
