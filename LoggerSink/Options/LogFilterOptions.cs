using Microsoft.Extensions.Logging;

namespace LoggerSink.Options;

public class LogFilterOptions
{
	public const string SectionName = "LogFilters";
	private IReadOnlyCollection<LogFilter> Filters { get; set; } = [];

	public class LogFilter
	{
		public string CategoryName { get; set; } // TODO: StartsWith vs Exact Match
		public LogLevel? LogLevel { get; set; }
		public LogLevel? MinLogLevel { get; set; }
		public LogLevel? MaxLogLevel { get; set; }
	}
}