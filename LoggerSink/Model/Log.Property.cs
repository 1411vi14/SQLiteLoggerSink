namespace LoggerSink.Model;

public partial class Log
{
	public abstract class Property // TPH
	{
		public int Id { get; set; }

		public int PropertyDefinitionId { get; init; }
		public PropertyDefinition? PropertyDefinition { get; init; }
	}

	public sealed class IntProperty : Property
	{
		public required long IntValue { get; init; }
	}

	public sealed class RealProperty : Property
	{
		public required double RealValue { get; init; }
	}

	public sealed class TextProperty : Property
	{
		public required string TextValue { get; init; }
	}

	public sealed class UuidProperty : Property
	{
		public required Guid UuidValue { get; init; }
	}

	public sealed class BlobProperty : Property
	{
		public required byte[] BlobValue { get; init; }
	}

	public sealed class AnyProperty : Property
	{
		public required object ObjectValue { get; init; } // BSON
	}
}