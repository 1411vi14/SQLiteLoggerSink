using System.Threading.Channels;

using LoggerSink.Options;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LoggerSink;

public sealed class Logger : ILogger, IDisposable
{
	private readonly string _categoryName;
	private readonly ChannelWriter<LogEvent> _channel;
	private readonly IOptionsMonitor<LogFilterOptions> _logFilterOptionsMonitor;
	private readonly Stack<KeyValuePair<string, object>> _propertyScopes = new();
	private readonly Stack<string> _scopes = new();

	public Logger(
		string categoryName,
		ChannelWriter<LogEvent> channel,
		IOptionsMonitor<LogFilterOptions> logFilterOptionsMonitor
	)
	{
		_categoryName = categoryName;
		_channel = channel;
		_logFilterOptionsMonitor = logFilterOptionsMonitor;
	}

	public void Dispose()
	{
		_scopes.Clear();
		_propertyScopes.Clear();
	}

	public void Log<TState>(
		LogLevel logLevel,
		EventId eventId,
		TState state,
		Exception? exception,
		Func<TState, Exception?, string> formatter
	)
	{
		if (!IsEnabled(logLevel))
		{
			return;
		}

		List<KeyValuePair<string, object?>> properties = _propertyScopes
			.Reverse()
			.Select(property => new KeyValuePair<string, object?>(property.Key, property.Value))
			.ToList();
		string messageTemplate = formatter(state, exception);

		if (state is IEnumerable<KeyValuePair<string, object?>> stateProperties)
		{
			foreach (KeyValuePair<string, object?> property in stateProperties)
			{
				if (string.Equals(property.Key, "{OriginalFormat}", StringComparison.Ordinal))
				{
					if (property.Value is string template)
					{
						messageTemplate = template;
					}

					continue;
				}

				properties.Add(property);
			}
		}

		_channel.WriteAsync(
			new LogEvent
			{
				TimeStamp = DateTimeOffset.Now,
				LogLevel = logLevel,
				CategoryName = _categoryName,
				Exception = exception,
				MessageTemplate = messageTemplate,
				Properties = properties,
				Scopes = _scopes.ToArray()
			}
		).GetAwaiter().GetResult();
	}

	public bool IsEnabled(LogLevel logLevel)
	{
		return _logFilterOptionsMonitor.CurrentValue.IsEnabled(_categoryName, logLevel);
	}

	public IDisposable? BeginScope<TState>(TState state) where TState : notnull
	{
		switch (state)
		{
			case string s:
				_scopes.Push(s);
				return new LoggerScope(_scopes);
			case IEnumerable<KeyValuePair<string, object>> properties:
				int propertyCount = 0;
				foreach (KeyValuePair<string, object> property in properties)
				{
					_propertyScopes.Push(property);
					propertyCount++;
				}

				return new PropertyScope(_propertyScopes, propertyCount);
			default:
				throw new NotSupportedException("Unsupported scope type");
		}
	}

	private class LoggerScope(Stack<string> scopes) : IDisposable
	{
		public void Dispose()
		{
			scopes.TryPop(out _);
		}
	}

	private class PropertyScope(Stack<KeyValuePair<string, object>> scopes, int propertyCount) : IDisposable
	{
		public void Dispose()
		{
			for (int i = 0; i < propertyCount; i++)
			{
				if (!scopes.TryPop(out _))
				{
					break;
				}
			}
		}
	}
}