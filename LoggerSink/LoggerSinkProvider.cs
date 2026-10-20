using System.Threading.Channels;

using Microsoft.Extensions.Logging;

namespace LoggerSink;

public class LoggerSinkProvider : ILoggerProvider
{
	private readonly ChannelWriter<LogEvent> _channelWriter;

	public LoggerSinkProvider(ChannelWriter<LogEvent> channelWriter)
	{
		_channelWriter = channelWriter;
	}

	public ILogger CreateLogger(string categoryName)
	{
		return new Logger(categoryName, _channelWriter);
	}

	public void Dispose()
	{
		_channelWriter.Complete();
	}
}