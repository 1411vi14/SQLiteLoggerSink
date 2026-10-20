namespace LoggerSink.Model;

public partial class Log
{
	public sealed class Definition
	{
		public int Id { get; set; }
		public required string Template { get; set; }
	}
}