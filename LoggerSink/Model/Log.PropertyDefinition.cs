namespace LoggerSink.Model;

public partial class Log
{
	public sealed class PropertyDefinition
	{
		public int Id { get; init; }
		public required string Name { get; init; }
		public required string Type { get; init; }
	}
}