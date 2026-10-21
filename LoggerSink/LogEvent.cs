using Microsoft.Extensions.Logging;

namespace LoggerSink;

public readonly record struct LogEvent
{
	public DateTimeOffset TimeStamp { get; init; }
	public LogLevel LogLevel { get; init; }
	public string CategoryName { get; init; }
	public Exception? Exception { get; init; }
	public string MessageTemplate { get; init; }
	public IReadOnlyCollection<KeyValuePair<string, object?>> Properties { get; init; }
	public IReadOnlyCollection<string> Scopes { get; init; }
}