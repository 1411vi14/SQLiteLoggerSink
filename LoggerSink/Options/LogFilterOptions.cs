using Microsoft.Extensions.Logging;

namespace LoggerSink.Options;

// TODO: Mit Microsoft.Extensions.Logging.LogLevel kompatibel?
public class LogFilterOptions
{
	public const string SectionName = "Logging";
	public Dictionary<string, LogLevel> LogLevel { get; set; } = []; // Microsoft.Extensions.Logging.LogLevel
	public IReadOnlyCollection<LogFilter> Filters { get; set; } = [];

	public class LogFilter
	{
		public string CategoryName { get; set; } // TODO: StartsWith vs Exact Match
		public LogLevel? LogLevel { get; set; }
		public LogLevel? MinLogLevel { get; set; }
		public LogLevel? MaxLogLevel { get; set; }
	}
}