using Microsoft.Data.Sqlite;

namespace LoggerSink.Options;

public class SqliteLogSinkOptions
{
	public uint BatchSize { get; set; }
	public uint QueueCapacity { get; set; }
	public IReadOnlyList<CustomColumn> CustomColumns { get; set; } = [];

	public readonly record struct CustomColumn(string ColumnName, string PropertyName, SqliteType DataType);
}