using System.Diagnostics;
using System.Threading.Channels;

using LoggerSink.Options;

using Microsoft.Data.Sqlite;

namespace LoggerSink.Tests;

public sealed class SqliteLogSinkTests
{
	[Test]
	public async Task Sink_MigratesCustomColumnsAndWritesStructuredBatches(CancellationToken cancellationToken)
	{
		string databasePath = Path.Combine(Path.GetTempPath(), $"sqlite-log-sink-{Guid.NewGuid():N}.db");

		try
		{
			CreateLegacyDatabase(databasePath);
			SqliteConnectionStringBuilder connectionString = new()
			{
				DataSource = databasePath, Mode = SqliteOpenMode.ReadWriteCreate
			};
			SqliteLogSinkOptions sinkOptions = new()
			{
				BatchSize = 3,
				QueueCapacity = 16,
				CustomColumns =
				[
					new SqliteLogSinkOptions.CustomColumn("OrderId", "OrderId", SqliteType.Integer)
				]
			};
			TestOptionsMonitor<SqliteConnectionStringBuilder> connectionOptionsMonitor = new(
				connectionString,
				(nameof(SqliteLogSink), connectionString)
			);
			TestOptionsMonitor<SqliteLogSinkOptions> sinkOptionsMonitor = new(sinkOptions);
			TestOptionsMonitor<LogFilterOptions> filterOptionsMonitor = new(
				new LogFilterOptions
				{
					LogLevel =
					{
						["Default"] = LogLevel.Information
					}
				}
			);
			Channel<LogEvent> channel = Channel.CreateBounded<LogEvent>(16);

			using SqliteLogSink sink = new(
				connectionOptionsMonitor,
				sinkOptionsMonitor,
				channel.Reader,
				filterOptionsMonitor
			);
			await sink.StartAsync(cancellationToken);

			using Logger logger = new("Orders.Worker", channel.Writer, filterOptionsMonitor);
			await Assert.That(logger.IsEnabled(LogLevel.Debug)).IsFalse();
			logger.LogInformation("Order {OrderId}", 123);
			logger.LogInformation("Order {OrderId}", 456);

			await WaitForEventCountAsync(databasePath, 3, TimeSpan.FromSeconds(5), cancellationToken);

			channel.Writer.Complete();
			using CancellationTokenSource stopTimeout = new(TimeSpan.FromSeconds(5));
			await sink.StopAsync(stopTimeout.Token);

			await using SqliteConnection verificationConnection = new(
				new SqliteConnectionStringBuilder
					{
						DataSource = databasePath, Mode = SqliteOpenMode.ReadOnly
					}
					.ConnectionString
			);
			await verificationConnection.OpenAsync(stopTimeout.Token);

			await Assert.That(await ScalarLongAsync(verificationConnection, "SELECT COUNT(*) FROM LogEvents;"))
				.IsEqualTo(3L);
			await Assert.That(
				await ScalarLongAsync(
					verificationConnection,
					"SELECT COUNT(*) FROM LogDefinitions WHERE Template = 'Order {OrderId}';"
				)
			).IsEqualTo(1L);
			await Assert.That(
				await ScalarLongAsync(
					verificationConnection,
					"SELECT COUNT(*) FROM LogProperties WHERE Name = 'OrderId';"
				)
			).IsEqualTo(3L);
			await Assert.That(
				await ScalarLongAsync(
					verificationConnection,
					"SELECT OrderId FROM LogEvents WHERE Id = 1;"
				)
			).IsEqualTo(7L);
			await Assert.That(
				await ScalarLongAsync(
					verificationConnection,
					"SELECT OrderId FROM LogEvents WHERE Id = 2;"
				)
			).IsEqualTo(123L);
			await Assert.That(
				await ScalarLongAsync(
					verificationConnection,
					"SELECT OrderId FROM LogEvents WHERE Id = 3;"
				)
			).IsEqualTo(456L);
			await Assert.That(
				await ScalarLongAsync(
					verificationConnection,
					"SELECT COUNT(*) FROM LogEvents WHERE DefinitionId = " +
					"(SELECT Id FROM LogDefinitions WHERE Template = 'Order {OrderId}');"
				)
			).IsEqualTo(2L);
		}
		finally
		{
			DeleteDatabaseFiles(databasePath);
		}
	}

	[Test]
	public async Task Sink_FlushesPartialBatchAfterTimeout(CancellationToken cancellationToken)
	{
		string databasePath = Path.Combine(Path.GetTempPath(), $"sqlite-log-sink-{Guid.NewGuid():N}.db");

		try
		{
			SqliteConnectionStringBuilder connectionString = new()
			{
				DataSource = databasePath, Mode = SqliteOpenMode.ReadWriteCreate
			};
			TestOptionsMonitor<SqliteConnectionStringBuilder> connectionOptionsMonitor = new(
				connectionString,
				(nameof(SqliteLogSink), connectionString)
			);
			TestOptionsMonitor<SqliteLogSinkOptions> sinkOptionsMonitor = new(
				new SqliteLogSinkOptions
				{
					BatchSize = 10, QueueCapacity = 8
				}
			);
			TestOptionsMonitor<LogFilterOptions> filterOptionsMonitor = new(new LogFilterOptions());
			Channel<LogEvent> channel = Channel.CreateBounded<LogEvent>(8);
			using SqliteLogSink sink = new(
				connectionOptionsMonitor,
				sinkOptionsMonitor,
				channel.Reader,
				filterOptionsMonitor
			);

			await sink.StartAsync(cancellationToken);
			await Assert.That(
				channel.Writer.TryWrite(
					new LogEvent
					{
						TimeStamp = DateTimeOffset.UtcNow,
						LogLevel = LogLevel.Information,
						CategoryName = "Test",
						MessageTemplate = "partial batch",
						Properties = [],
						Scopes = []
					}
				)
			).IsTrue();

			await WaitForEventCountAsync(databasePath, 1, TimeSpan.FromSeconds(5), cancellationToken);

			channel.Writer.Complete();
			using CancellationTokenSource stopTimeout = new(TimeSpan.FromSeconds(5));
			await sink.StopAsync(stopTimeout.Token);
		}
		finally
		{
			DeleteDatabaseFiles(databasePath);
		}
	}

	private static void CreateLegacyDatabase(string databasePath)
	{
		using SqliteConnection connection = new(
			new SqliteConnectionStringBuilder
				{
					DataSource = databasePath, Mode = SqliteOpenMode.ReadWriteCreate
				}
				.ConnectionString
		);
		connection.Open();
		using SqliteCommand command = connection.CreateCommand();
		command.CommandText = """
							CREATE TABLE LogDefinitions (Id INTEGER PRIMARY KEY, Template TEXT NOT NULL UNIQUE);
							CREATE TABLE LogEvents (
								Id INTEGER PRIMARY KEY,
								Timestamp TEXT NOT NULL,
								Level INTEGER NOT NULL,
								CategoryName TEXT NOT NULL,
								DefinitionId INTEGER NOT NULL,
								Exception TEXT NULL,
								Scopes TEXT NOT NULL
							);
							CREATE TABLE LogProperties (
								Id INTEGER PRIMARY KEY,
								EventId INTEGER NOT NULL,
								Name TEXT NOT NULL,
								Value,
								ValueType TEXT NOT NULL
							);
							INSERT INTO LogDefinitions (Id, Template) VALUES (1, 'legacy');
							INSERT INTO LogEvents (Id, Timestamp, Level, CategoryName, DefinitionId, Scopes)
								VALUES (1, '2026-01-01T00:00:00+00:00', 2, 'Legacy', 1, '[]');
							INSERT INTO LogProperties (Id, EventId, Name, Value, ValueType)
								VALUES (1, 1, 'OrderId', 7, 'System.Int32');
							""";
		command.ExecuteNonQuery();
	}

	private static async Task WaitForEventCountAsync(
		string databasePath,
		long expectedCount,
		TimeSpan timeout,
		CancellationToken cancellationToken
	)
	{
		Stopwatch stopwatch = Stopwatch.StartNew();

		while (stopwatch.Elapsed < timeout)
		{
			if (File.Exists(databasePath))
			{
				await using SqliteConnection connection = new(
					new SqliteConnectionStringBuilder
					{
						DataSource = databasePath, Mode = SqliteOpenMode.ReadOnly
					}.ConnectionString
				);
				await connection.OpenAsync(cancellationToken);

				if (await ScalarLongAsync(connection, "SELECT COUNT(*) FROM LogEvents;") >= expectedCount)
				{
					return;
				}
			}

			await Task.Delay(50, cancellationToken);
		}

		Assert.Fail($"Timeout waiting for {expectedCount} log events in database '{databasePath}'.");
	}

	private static async Task<long> ScalarLongAsync(SqliteConnection connection, string sql)
	{
		await using SqliteCommand command = connection.CreateCommand();
		command.CommandText = sql;
		return (long)(await command.ExecuteScalarAsync()
					?? throw new InvalidOperationException("Expected a scalar query result."));
	}

	private static void DeleteDatabaseFiles(string databasePath)
	{
		foreach (string file in new[]
				{
					databasePath, $"{databasePath}-shm", $"{databasePath}-wal"
				})
		{
			if (File.Exists(file))
			{
				File.Delete(file);
			}
		}
	}

	private sealed class TestOptionsMonitor<T>(T currentValue, params (string Name, T Value)[] namedOptions)
		: IOptionsMonitor<T>
	{
		private readonly Dictionary<string, T> _namedOptions = namedOptions.ToDictionary(
			option => option.Name,
			option => option.Value,
			StringComparer.Ordinal
		);

		public T CurrentValue => currentValue;

		public T Get(string? name)
		{
			return name is not null && _namedOptions.TryGetValue(name, out T? value) ? value : currentValue;
		}

		public IDisposable? OnChange(Action<T, string?> listener)
		{
			return null;
		}
	}
}