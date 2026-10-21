using System.Threading.Channels;

using LoggerSink.Options;

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LoggerSink.Extensions;

public static class LoggingBuilderExtensions
{
	extension(ILoggingBuilder builder)
	{
		public ILoggingBuilder AddSqliteLogSink(
			Action<SqliteConnectionStringBuilder> sqliteConnectionConfigure,
			Action<SqliteLogSinkOptions> configure
		)
		{
			builder.Services.Configure(nameof(SqliteLogSink), sqliteConnectionConfigure);
			builder.Services.Configure(configure);
			builder.Services.AddOptions<LogFilterOptions>().BindConfiguration(LogFilterOptions.SectionName);
			builder.Services.AddSingleton<ILoggerProvider, LoggerSinkProvider>();
			builder.Services.AddSingleton<Channel<LogEvent>>(sp =>
				{
					SqliteLogSinkOptions options =
						sp.GetRequiredService<IOptionsMonitor<SqliteLogSinkOptions>>().CurrentValue;
					return Channel.CreateBounded<LogEvent>(
						new BoundedChannelOptions((int)options.QueueCapacity)
						{
							FullMode = BoundedChannelFullMode.Wait
						}
					);
				}
			);
			builder.Services.AddSingleton<ChannelWriter<LogEvent>>(sp =>
				{
					Channel<LogEvent> channel = sp.GetRequiredService<Channel<LogEvent>>();
					return channel.Writer;
				}
			);
			builder.Services.AddSingleton<ChannelReader<LogEvent>>(sp =>
				{
					Channel<LogEvent> channel = sp.GetRequiredService<Channel<LogEvent>>();
					return channel.Reader;
				}
			);
			builder.Services.AddHostedService<SqliteLogSink>();
			return builder;
		}
	}
}