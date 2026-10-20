using System.Threading.Channels;

using LoggerSink.Options;

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LoggerSink;

public sealed class SqliteLogSink : BackgroundService
{
	private readonly ChannelReader<LogEvent> _channelReader;
	private readonly IOptionsMonitor<LogFilterOptions> _logFilterOptionsMonitor;
	private readonly IOptionsMonitor<SqliteConnectionStringBuilder> _sqliteConnectionOptionsMonitor;
	private readonly IDisposable? _sqliteLogSinkOptionsMonitorOnChangeListener;
	private SqliteConnection? _connection;

	public SqliteLogSink(
		[FromKeyedServices(nameof(SqliteLogSink))]
		IOptionsMonitor<SqliteConnectionStringBuilder> sqliteConnectionOptionsMonitor,
		IOptionsMonitor<SqliteLogSinkOptions> sqliteLogSinkOptionsMonitor,
		ChannelReader<LogEvent> channelReader,
		IOptionsMonitor<LogFilterOptions> logFilterOptionsMonitor
	)
	{
		_sqliteConnectionOptionsMonitor = sqliteConnectionOptionsMonitor;
		_channelReader = channelReader;
		_logFilterOptionsMonitor = logFilterOptionsMonitor;

		// Initialize the SQLite
		RecreateSqliteConnection(sqliteLogSinkOptionsMonitor.CurrentValue);
		_sqliteLogSinkOptionsMonitorOnChangeListener = sqliteLogSinkOptionsMonitor.OnChange(RecreateSqliteConnection);
	}

	private void RecreateSqliteConnection(SqliteLogSinkOptions arg1)
	{
		SqliteConnection connection = new(_sqliteConnectionOptionsMonitor.CurrentValue.ConnectionString);
		// TODO: Migrate the database schema if necessary
		// TODO: Add the custom columns to the database schema if necessary und Migrate existing Properties to the new columns
		SqliteConnection? oldConnection = _connection;
		_connection = connection;
		oldConnection?.Dispose();
	}

	protected override async Task ExecuteAsync(CancellationToken stoppingToken)
	{
		await foreach (LogEvent logEvent in _channelReader.ReadAllAsync(stoppingToken))
		{
			if (!EvaluateFilter(logEvent))
			{
				continue;
			}

			// TODO: Implement the method to write the log event to the SQLite database (Optimised, Batched, Cache DefinitionIds, etc.)
			await WriteLogEventToDatabaseAsync(
				logEvent,
				stoppingToken
			);
		}
	}

	private async Task WriteLogEventToDatabaseAsync(LogEvent logEvent, CancellationToken stoppingToken)
	{
		// TODO: Als Batch oder spätestens nach Timeout schreiben, um die Performance zu verbessern. Außerdem DefinitionIds cachen, um die Performance zu verbessern.
		// Beim wechseln der LogFilterOptions muss die Datenbank ggf. angepasst werden
		throw new NotImplementedException();
	}

	private bool EvaluateFilter(LogEvent logEvent)
	{
		const string DefaultCategoryName = "Default";

		if (_logFilterOptionsMonitor.CurrentValue.LogLevel.TryGetValue(
				logEvent.CategoryName,
				out LogLevel categoryLogLevel
			) && logEvent.LogLevel < categoryLogLevel)
		{
			return false;
		}

		if (_logFilterOptionsMonitor.CurrentValue.LogLevel
				.Where(l => logEvent.CategoryName.StartsWith(l.Key))
				.OrderByDescending(l => l.Key.Length).FirstOrDefault() is KeyValuePair<string, LogLevel> filter)
		{
			_logFilterOptionsMonitor.CurrentValue.LogLevel.TryAdd(logEvent.CategoryName, filter.Value);

			if (logEvent.LogLevel < filter.Value)
			{
				return false;
			}
		}

		if (_logFilterOptionsMonitor.CurrentValue.LogLevel.TryGetValue(
				DefaultCategoryName,
				out LogLevel defaultLogLevel
			) && logEvent.LogLevel < defaultLogLevel)
		{
			return false;
		}

		return true;
	}

	public override void Dispose()
	{
		base.Dispose();
		_sqliteLogSinkOptionsMonitorOnChangeListener?.Dispose();
		_connection?.Dispose();
	}
}