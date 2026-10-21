using LoggerSink.Options;

using System.Threading.Tasks;

namespace LoggerSink.Tests;

public sealed class LogFilterOptionsTests
{
	[Test]
	public async Task IsEnabled_UsesMostSpecificCategoryLevel()
	{
		LogFilterOptions options = new();
		options.LogLevel["Default"] = LogLevel.Information;
		options.LogLevel["Microsoft"] = LogLevel.Warning;
		options.LogLevel["Microsoft.Hosting"] = LogLevel.Error;

		await Assert.That(options.IsEnabled("Microsoft.Hosting.Lifetime", LogLevel.Warning)).IsFalse();
		await Assert.That(options.IsEnabled("Microsoft.Hosting.Lifetime", LogLevel.Error)).IsTrue();
		await Assert.That(options.IsEnabled("Microsoft.AspNetCore", LogLevel.Warning)).IsTrue();
		await Assert.That(options.IsEnabled("Application", LogLevel.Debug)).IsFalse();
	}

	[Test]
	public async Task IsEnabled_RejectsNoneAndHonorsExactCategory()
	{
		LogFilterOptions options = new();
		options.LogLevel["Default"] = LogLevel.Trace;
		options.LogLevel["Application.Worker"] = LogLevel.Error;

		await Assert.That(options.IsEnabled("Application.Worker", LogLevel.Warning)).IsFalse();
		await Assert.That(options.IsEnabled("Application.Worker", LogLevel.Critical)).IsTrue();
		await Assert.That(options.IsEnabled("Application.Other", LogLevel.None)).IsFalse();
	}

	[Test]
	public async Task IsEnabled_AppliesMatchingFilterConstraints()
	{
		LogFilterOptions options = new()
		{
			Filters =
			[
				new LogFilterOptions.LogFilter
				{
					CategoryName = "Orders",
					MinLogLevel = LogLevel.Warning,
					MaxLogLevel = LogLevel.Error
				}
			]
		};

		await Assert.That(options.IsEnabled("Orders.Handler", LogLevel.Information)).IsFalse();
		await Assert.That(options.IsEnabled("Orders.Handler", LogLevel.Warning)).IsTrue();
		await Assert.That(options.IsEnabled("Orders.Handler", LogLevel.Error)).IsTrue();
		await Assert.That(options.IsEnabled("Orders.Handler", LogLevel.Critical)).IsFalse();
		await Assert.That(options.IsEnabled("Users.Handler", LogLevel.Debug)).IsTrue();
	}
}