using System.Threading.Channels;

using LoggerSink.Options;

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace LoggerSink;

public sealed class SqliteLogSink : BackgroundService
{
	private readonly ChannelReader<LogEvent> _channelReader;
	private readonly IOptionsMonitor<LogFilterOptions> _optionsMonitor;
	private readonly IOptionsMonitor<SqliteConnectionStringBuilder> _sqliteConnectionOptionsMonitor;
	private readonly IDisposable? _sqliteLogSinkOptionsMonitorOnChangeListener;
	private SqliteConnection? _connection;

	public SqliteLogSink(
		[FromKeyedServices(nameof(SqliteLogSink))]
		IOptionsMonitor<SqliteConnectionStringBuilder> sqliteConnectionOptionsMonitor,
		IOptionsMonitor<SqliteLogSinkOptions> sqliteLogSinkOptionsMonitor,
		ChannelReader<LogEvent> channelReader,
		IOptionsMonitor<LogFilterOptions> optionsMonitor
	)
	{
		_sqliteConnectionOptionsMonitor = sqliteConnectionOptionsMonitor;
		_channelReader = channelReader;
		_optionsMonitor = optionsMonitor;

		// Initialize the SQLite
		RecreateSqliteConnection(sqliteLogSinkOptionsMonitor.CurrentValue);
		_sqliteLogSinkOptionsMonitorOnChangeListener = sqliteLogSinkOptionsMonitor.OnChange(RecreateSqliteConnection);
	}

	private void RecreateSqliteConnection(SqliteLogSinkOptions arg1)
	{
		SqliteConnection connection = new(_sqliteConnectionOptionsMonitor.CurrentValue.ConnectionString);
		// TODO: Migrate the database schema if necessary
		SqliteConnection? oldConnection = _connection;
		_connection = connection;
		oldConnection?.Dispose();
	}

	protected override async Task ExecuteAsync(CancellationToken stoppingToken)
	{
		await foreach (LogEvent logEvent in _channelReader.ReadAllAsync(stoppingToken))
		{
			if (!_optionsMonitor.CurrentValue.IsLogLevelEnabled(logEvent.LogLevel))
			{
				continue;
			}

			// TODO: Evaluate Filter

			// TODO: Implement the method to write the log event to the SQLite database (Optimised, Batched, Cache DefinitionIds, etc.)
			await WriteLogEventToDatabaseAsync(
				logEvent,
				stoppingToken
			);
		}
	}

	public override void Dispose()
	{
		base.Dispose();
		_sqliteLogSinkOptionsMonitorOnChangeListener?.Dispose();
		_connection?.Dispose();
	}
}