using Microsoft.Extensions.Logging;

namespace LoggerSink.Model;

public partial class Log
{
	public sealed class Event
	{
		public int Id { get; init; }
		public required LogLevel Level { get; init; }
		public required string LoggerName { get; init; }
		public int DefinitionId { get; init; }
		public Definition? Definition { get; init; }
		public required List<Property> Properties { get; init; } = [];
	}
}