using System.Globalization;
using System.Text.Json;
using System.Threading.Channels;

using LoggerSink.Options;

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace LoggerSink;

public sealed class SqliteLogSink : BackgroundService
{
	private static readonly TimeSpan BatchFlushInterval = TimeSpan.FromSeconds(1);

	private readonly ChannelReader<LogEvent> _channelReader;
	private readonly IOptionsMonitor<LogFilterOptions> _logFilterOptionsMonitor;
	private readonly IOptionsMonitor<SqliteConnectionStringBuilder> _sqliteConnectionOptionsMonitor;
	private readonly SemaphoreSlim _databaseLock = new(1, 1);
	private readonly Dictionary<string, long> _definitionIds = new(StringComparer.Ordinal);
	private readonly IDisposable? _sqliteLogSinkOptionsMonitorOnChangeListener;
	private SqliteConnection? _connection;
	private IReadOnlyList<SqliteLogSinkOptions.CustomColumn> _customColumns = [];
	private int _batchSize = 1;

	public SqliteLogSink(
		IOptionsMonitor<SqliteConnectionStringBuilder> sqliteConnectionOptionsMonitor,
		IOptionsMonitor<SqliteLogSinkOptions> sqliteLogSinkOptionsMonitor,
		ChannelReader<LogEvent> channelReader,
		IOptionsMonitor<LogFilterOptions> logFilterOptionsMonitor
	)
	{
		_sqliteConnectionOptionsMonitor = sqliteConnectionOptionsMonitor;
		_channelReader = channelReader;
		_logFilterOptionsMonitor = logFilterOptionsMonitor;

		RecreateSqliteConnection(sqliteLogSinkOptionsMonitor.CurrentValue);
		_sqliteLogSinkOptionsMonitorOnChangeListener = sqliteLogSinkOptionsMonitor.OnChange(RecreateSqliteConnection);
	}

	private void RecreateSqliteConnection(SqliteLogSinkOptions options)
	{
		_databaseLock.Wait();
		try
		{
			SqliteConnection connection = new(
				_sqliteConnectionOptionsMonitor.Get(nameof(SqliteLogSink)).ConnectionString
			);
			try
			{
				connection.Open();
				InitializeSchema(connection, options.CustomColumns);

				SqliteConnection? oldConnection = _connection;
				_connection = connection;
				_customColumns = options.CustomColumns.ToArray();
				_batchSize = options.BatchSize > int.MaxValue ? int.MaxValue : Math.Max(1, (int)options.BatchSize);
				_definitionIds.Clear();
				LoadDefinitionIds(connection);
				oldConnection?.Dispose();
			}
			catch
			{
				connection.Dispose();
				throw;
			}
		}
		finally
		{
			_databaseLock.Release();
		}
	}

	protected override async Task ExecuteAsync(CancellationToken stoppingToken)
	{
		while (!stoppingToken.IsCancellationRequested)
		{
			bool hasData;
			try
			{
				hasData = await _channelReader.WaitToReadAsync(stoppingToken);
			}
			catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
			{
				break;
			}

			if (!hasData)
			{
				break;
			}

			int batchSize = _batchSize;
			List<LogEvent> batch = [];
			bool channelCompleted = false;

			while (batch.Count < batchSize)
			{
				while (_channelReader.TryRead(out LogEvent logEvent))
				{
					if (_logFilterOptionsMonitor.CurrentValue.IsEnabled(logEvent.CategoryName, logEvent.LogLevel))
					{
						batch.Add(logEvent);
						if (batch.Count >= batchSize)
						{
							break;
						}
					}
				}

				if (batch.Count >= batchSize)
				{
					break;
				}

				using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
				timeout.CancelAfter(BatchFlushInterval);
				try
				{
					if (!await _channelReader.WaitToReadAsync(timeout.Token))
					{
						channelCompleted = true;
						break;
					}
				}
				catch (OperationCanceledException) when (timeout.IsCancellationRequested)
				{
					if (stoppingToken.IsCancellationRequested)
					{
						channelCompleted = true;
					}

					break;
				}
			}

			if (batch.Count > 0)
			{
				await WriteBatchAsync(batch, CancellationToken.None);
			}

			if (channelCompleted)
			{
				break;
			}
		}
	}

	private async Task WriteBatchAsync(IReadOnlyList<LogEvent> logEvents, CancellationToken cancellationToken)
	{
		await _databaseLock.WaitAsync(cancellationToken);
		try
		{
			SqliteConnection connection = _connection
				?? throw new InvalidOperationException("The SQLite connection has not been initialized.");
			using SqliteTransaction transaction = connection.BeginTransaction();
			using SqliteCommand eventCommand = connection.CreateCommand();
			eventCommand.Transaction = transaction;
			BuildEventInsertCommand(eventCommand);
			eventCommand.Prepare();

			using SqliteCommand definitionCommand = connection.CreateCommand();
			definitionCommand.Transaction = transaction;
			definitionCommand.CommandText = "INSERT OR IGNORE INTO LogDefinitions (Template) VALUES ($template);";
			SqliteParameter templateParameter = definitionCommand.Parameters.Add("$template", SqliteType.Text);
			definitionCommand.Prepare();
			using SqliteCommand definitionIdCommand = connection.CreateCommand();
			definitionIdCommand.Transaction = transaction;
			definitionIdCommand.CommandText = "SELECT Id FROM LogDefinitions WHERE Template = $template;";
			SqliteParameter definitionIdTemplateParameter =
				definitionIdCommand.Parameters.Add("$template", SqliteType.Text);
			definitionIdCommand.Prepare();

			using SqliteCommand propertyCommand = connection.CreateCommand();
			propertyCommand.Transaction = transaction;
			propertyCommand.CommandText =
				"INSERT INTO LogProperties (EventId, Name, Value, ValueType) " +
				"VALUES ($eventId, $name, $value, $valueType);";
			SqliteParameter eventIdParameter = propertyCommand.Parameters.Add("$eventId", SqliteType.Integer);
			SqliteParameter nameParameter = propertyCommand.Parameters.Add("$name", SqliteType.Text);
			SqliteParameter valueParameter = propertyCommand.Parameters.Add("$value", SqliteType.Text);
			SqliteParameter valueTypeParameter = propertyCommand.Parameters.Add("$valueType", SqliteType.Text);
			propertyCommand.Prepare();

			foreach (LogEvent logEvent in logEvents)
			{
				long definitionId = GetDefinitionId(
					logEvent.MessageTemplate,
					definitionCommand,
					templateParameter,
					definitionIdCommand,
					definitionIdTemplateParameter
				);
				PopulateEventInsertCommand(eventCommand, logEvent, definitionId);
				await eventCommand.ExecuteNonQueryAsync(cancellationToken);

				if (logEvent.Properties.Count == 0)
				{
					continue;
				}

				using SqliteCommand lastIdCommand = connection.CreateCommand();
				lastIdCommand.Transaction = transaction;
				lastIdCommand.CommandText = "SELECT last_insert_rowid();";
				long eventId = (long)(await lastIdCommand.ExecuteScalarAsync(cancellationToken)
					?? throw new InvalidOperationException("SQLite did not return the inserted log event ID."));
				eventIdParameter.Value = eventId;

				foreach (KeyValuePair<string, object?> property in logEvent.Properties)
				{
					nameParameter.Value = property.Key;
					object sqliteValue = ToSqliteValue(property.Value);
					valueParameter.SqliteType = GetValueSqliteType(sqliteValue);
					valueParameter.Value = sqliteValue;
					valueTypeParameter.Value = property.Value?.GetType().FullName ?? "null";
					await propertyCommand.ExecuteNonQueryAsync(cancellationToken);
				}
			}

			await transaction.CommitAsync(cancellationToken);
		}
		finally
		{
			_databaseLock.Release();
		}
	}

	private void BuildEventInsertCommand(SqliteCommand command)
	{
		List<string> columns =
		[
			"Timestamp",
			"Level",
			"CategoryName",
			"DefinitionId",
			"Exception",
			"Scopes"
		];
		List<string> values = ["$timestamp", "$level", "$categoryName", "$definitionId", "$exception", "$scopes"];
		command.Parameters.Add("$timestamp", SqliteType.Text);
		command.Parameters.Add("$level", SqliteType.Integer);
		command.Parameters.Add("$categoryName", SqliteType.Text);
		command.Parameters.Add("$definitionId", SqliteType.Integer);
		command.Parameters.Add("$exception", SqliteType.Text);
		command.Parameters.Add("$scopes", SqliteType.Text);

		for (int i = 0; i < _customColumns.Count; i++)
		{
			columns.Add(QuoteIdentifier(_customColumns[i].ColumnName));
			values.Add($"$custom{i}");
			command.Parameters.Add($"$custom{i}", _customColumns[i].DataType);
		}

		command.CommandText =
			$"INSERT INTO LogEvents ({string.Join(", ", columns)}) VALUES ({string.Join(", ", values)});";
	}

	private void PopulateEventInsertCommand(SqliteCommand command, LogEvent logEvent, long definitionId)
	{
		command.Parameters["$timestamp"].Value = logEvent.TimeStamp.ToString("O", CultureInfo.InvariantCulture);
		command.Parameters["$level"].Value = (int)logEvent.LogLevel;
		command.Parameters["$categoryName"].Value = logEvent.CategoryName;
		command.Parameters["$definitionId"].Value = definitionId;
		command.Parameters["$exception"].Value = logEvent.Exception?.ToString() ?? (object)DBNull.Value;
		command.Parameters["$scopes"].Value = JsonSerializer.Serialize(logEvent.Scopes);

		for (int i = 0; i < _customColumns.Count; i++)
		{
			KeyValuePair<string, object?> property = logEvent.Properties.FirstOrDefault(
				p => string.Equals(p.Key, _customColumns[i].PropertyName, StringComparison.Ordinal)
			);
			command.Parameters[$"$custom{i}"].Value =
				property.Key is null ? DBNull.Value : ToSqliteValue(property.Value);
		}
	}

	private long GetDefinitionId(
		string template,
		SqliteCommand insertCommand,
		SqliteParameter templateParameter,
		SqliteCommand selectCommand,
		SqliteParameter selectTemplateParameter
	)
	{
		if (_definitionIds.TryGetValue(template, out long definitionId))
		{
			return definitionId;
		}

		templateParameter.Value = template;
		insertCommand.ExecuteNonQuery();
		selectTemplateParameter.Value = template;
		definitionId = (long)(selectCommand.ExecuteScalar()
			?? throw new InvalidOperationException("SQLite did not return the log definition ID."));
		_definitionIds.Add(template, definitionId);
		return definitionId;
	}

	private static void InitializeSchema(
		SqliteConnection connection,
		IReadOnlyList<SqliteLogSinkOptions.CustomColumn> customColumns
	)
	{
		ValidateCustomColumns(customColumns);

		using SqliteCommand command = connection.CreateCommand();
		command.CommandText = """
			CREATE TABLE IF NOT EXISTS LogDefinitions (
				Id INTEGER PRIMARY KEY,
				Template TEXT NOT NULL UNIQUE
			);
			CREATE TABLE IF NOT EXISTS LogEvents (
				Id INTEGER PRIMARY KEY,
				Timestamp TEXT NOT NULL,
				Level INTEGER NOT NULL,
				CategoryName TEXT NOT NULL,
				DefinitionId INTEGER NOT NULL REFERENCES LogDefinitions(Id),
				Exception TEXT NULL,
				Scopes TEXT NOT NULL
			);
			CREATE TABLE IF NOT EXISTS LogProperties (
				Id INTEGER PRIMARY KEY,
				EventId INTEGER NOT NULL REFERENCES LogEvents(Id) ON DELETE CASCADE,
				Name TEXT NOT NULL,
				Value,
				ValueType TEXT NOT NULL
			);
			CREATE INDEX IF NOT EXISTS IX_LogEvents_Timestamp ON LogEvents(Timestamp);
			CREATE INDEX IF NOT EXISTS IX_LogEvents_CategoryName ON LogEvents(CategoryName);
			CREATE INDEX IF NOT EXISTS IX_LogProperties_EventId_Name ON LogProperties(EventId, Name);
			""";
		command.ExecuteNonQuery();

		HashSet<string> existingColumns = new(StringComparer.OrdinalIgnoreCase);
		using SqliteCommand columnsCommand = connection.CreateCommand();
		columnsCommand.CommandText = "PRAGMA table_info(LogEvents);";
		{
			using SqliteDataReader reader = columnsCommand.ExecuteReader();
			while (reader.Read())
			{
				existingColumns.Add(reader.GetString(1));
			}
		}

		foreach (SqliteLogSinkOptions.CustomColumn customColumn in customColumns)
		{
			if (existingColumns.Contains(customColumn.ColumnName))
			{
				continue;
			}

			using SqliteCommand addColumnCommand = connection.CreateCommand();
			addColumnCommand.CommandText =
				$"ALTER TABLE LogEvents ADD COLUMN {QuoteIdentifier(customColumn.ColumnName)} " +
				$"{GetSqliteTypeName(customColumn.DataType)};";
			addColumnCommand.ExecuteNonQuery();

			using SqliteCommand migrateCommand = connection.CreateCommand();
			migrateCommand.CommandText =
				$"UPDATE LogEvents SET {QuoteIdentifier(customColumn.ColumnName)} = (" +
				"SELECT Value FROM LogProperties WHERE EventId = LogEvents.Id AND Name = $propertyName LIMIT 1) " +
				"WHERE EXISTS (SELECT 1 FROM LogProperties WHERE EventId = LogEvents.Id AND Name = $propertyName);";
			migrateCommand.Parameters.AddWithValue("$propertyName", customColumn.PropertyName);
			migrateCommand.ExecuteNonQuery();
			existingColumns.Add(customColumn.ColumnName);
		}
	}

	private static void ValidateCustomColumns(IReadOnlyList<SqliteLogSinkOptions.CustomColumn> customColumns)
	{
		HashSet<string> names = new(StringComparer.OrdinalIgnoreCase)
		{
			"Id",
			"Timestamp",
			"Level",
			"CategoryName",
			"DefinitionId",
			"Exception",
			"Scopes"
		};

		foreach (SqliteLogSinkOptions.CustomColumn customColumn in customColumns)
		{
			if (string.IsNullOrWhiteSpace(customColumn.ColumnName))
			{
				throw new InvalidOperationException("Custom SQLite column names cannot be empty.");
			}

			if (string.IsNullOrWhiteSpace(customColumn.PropertyName))
			{
				throw new InvalidOperationException("Custom SQLite property names cannot be empty.");
			}

			if (!names.Add(customColumn.ColumnName))
			{
				throw new InvalidOperationException($"Duplicate or reserved custom SQLite column: {customColumn.ColumnName}");
			}

			_ = GetSqliteTypeName(customColumn.DataType);
		}
	}

	private static string GetSqliteTypeName(SqliteType type)
	{
		return type switch
		{
			SqliteType.Integer => "INTEGER",
			SqliteType.Real => "REAL",
			SqliteType.Text => "TEXT",
			SqliteType.Blob => "BLOB",
			_ => throw new InvalidOperationException($"Unsupported SQLite custom column type: {type}.")
		};
	}

	private static string QuoteIdentifier(string identifier)
	{
		return $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
	}

	private static object ToSqliteValue(object? value)
	{
		return value switch
		{
			null => DBNull.Value,
			string or byte[] or long or int or short or byte or bool or double or float or decimal or
				Guid or DateTime or DateTimeOffset => value,
			Enum enumValue => Convert.ToInt64(enumValue, CultureInfo.InvariantCulture),
			_ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? value.GetType().FullName ?? value.GetType().Name
		};
	}

	private static SqliteType GetValueSqliteType(object value)
	{
		return value switch
		{
			byte[] => SqliteType.Blob,
			double or float => SqliteType.Real,
			long or int or short or byte or bool => SqliteType.Integer,
			_ => SqliteType.Text
		};
	}

	private void LoadDefinitionIds(SqliteConnection connection)
	{
		using SqliteCommand command = connection.CreateCommand();
		command.CommandText = "SELECT Id, Template FROM LogDefinitions;";
		using SqliteDataReader reader = command.ExecuteReader();
		while (reader.Read())
		{
			_definitionIds.Add(reader.GetString(1), reader.GetInt64(0));
		}
	}

	public override void Dispose()
	{
		base.Dispose();
		_sqliteLogSinkOptionsMonitorOnChangeListener?.Dispose();
		_databaseLock.Wait();
		try
		{
			_connection?.Dispose();
			_connection = null;
		}
		finally
		{
			_databaseLock.Release();
		}
	}
}
