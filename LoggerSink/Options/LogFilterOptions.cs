using Microsoft.Extensions.Logging;

namespace LoggerSink.Options;

public class LogFilterOptions
{
	public const string SectionName = "Logging";
	public Dictionary<string, LogLevel> LogLevel { get; set; } = [];
	public IReadOnlyCollection<LogFilter> Filters { get; set; } = [];

	public bool IsEnabled(string categoryName, LogLevel logLevel)
	{
		if (logLevel == Microsoft.Extensions.Logging.LogLevel.None)
		{
			return false;
		}

		if (LogLevel.TryGetValue(categoryName, out Microsoft.Extensions.Logging.LogLevel exactLevel) &&
			logLevel < exactLevel)
		{
			return false;
		}

		KeyValuePair<string, Microsoft.Extensions.Logging.LogLevel> categoryFilter = LogLevel
			.Where(filter =>
				!string.Equals(filter.Key, "Default", StringComparison.OrdinalIgnoreCase) &&
				categoryName.StartsWith(filter.Key, StringComparison.Ordinal))
			.OrderByDescending(filter => filter.Key.Length)
			.FirstOrDefault();

		if (categoryFilter.Key is not null && logLevel < categoryFilter.Value)
		{
			return false;
		}

		if (categoryFilter.Key is null &&
			LogLevel.TryGetValue("Default", out Microsoft.Extensions.Logging.LogLevel defaultLevel) &&
			logLevel < defaultLevel)
		{
			return false;
		}

		LogFilter[] matchingFilters = Filters
			.Where(filter => categoryName.StartsWith(filter.CategoryName, StringComparison.Ordinal))
			.ToArray();
		return matchingFilters.Length == 0 || matchingFilters.Any(filter =>
			(!filter.LogLevel.HasValue || logLevel == filter.LogLevel.Value) &&
			(!filter.MinLogLevel.HasValue || logLevel >= filter.MinLogLevel.Value) &&
			(!filter.MaxLogLevel.HasValue || logLevel <= filter.MaxLogLevel.Value));
	}

	public class LogFilter
	{
		public string CategoryName { get; set; } = string.Empty;
		public LogLevel? LogLevel { get; set; }
		public LogLevel? MinLogLevel { get; set; }
		public LogLevel? MaxLogLevel { get; set; }
	}
}