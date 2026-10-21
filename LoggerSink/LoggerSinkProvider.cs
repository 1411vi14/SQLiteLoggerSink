using System.Threading.Channels;

using LoggerSink.Options;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LoggerSink;

public class LoggerSinkProvider : ILoggerProvider
{
	private readonly ChannelWriter<LogEvent> _channelWriter;
	private readonly IOptionsMonitor<LogFilterOptions> _logFilterOptionsMonitor;

	public LoggerSinkProvider(
		ChannelWriter<LogEvent> channelWriter,
		IOptionsMonitor<LogFilterOptions> logFilterOptionsMonitor
	)
	{
		_channelWriter = channelWriter;
		_logFilterOptionsMonitor = logFilterOptionsMonitor;
	}

	public ILogger CreateLogger(string categoryName)
	{
		return new Logger(categoryName, _channelWriter, _logFilterOptionsMonitor);
	}

	public void Dispose()
	{
		_channelWriter.Complete();
	}
}