using LoggerSink.Extensions;
using LoggerSink.Options;

using Microsoft.Data.Sqlite;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

builder.Logging
	.AddConsole()
	.AddSqliteLogSink(
		sqLiteConfig =>
		{
			sqLiteConfig.DataSource = "logs.db";
			sqLiteConfig.Mode = SqliteOpenMode.ReadWriteCreate;
			sqLiteConfig.Pooling = true;
		},
		options =>
		{
			options.BatchSize = 256;
			options.QueueCapacity = 8192;
			options.CustomColumns =
			[
				new SqliteLogSinkOptions.CustomColumn(
					"OrderId",
					"OrderId",
					SqliteType.Integer
				),
				new SqliteLogSinkOptions.CustomColumn(
					"CustomerId",
					"CustomerId",
					SqliteType.Integer
				),
				new SqliteLogSinkOptions.CustomColumn(
					"DurationMs",
					"DurationMs",
					SqliteType.Real
				)
			];
		}
	);
// Add services to the container.
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

WebApplication app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
	app.MapOpenApi();
}

app.UseHttpsRedirection();

string[] summaries = new[]
{
	"Freezing", "Bracing", "Chilly", "Cool", "Mild", "Warm", "Balmy", "Hot", "Sweltering", "Scorching"
};

app.MapGet(
		"/weatherforecast",
		() =>
		{
			WeatherForecast[] forecast = Enumerable.Range(1, 5).Select(index =>
					new WeatherForecast(
						DateOnly.FromDateTime(DateTime.Now.AddDays(index)),
						Random.Shared.Next(-20, 55),
						summaries[Random.Shared.Next(summaries.Length)]
					)
				)
				.ToArray();
			return forecast;
		}
	)
	.WithName("GetWeatherForecast");

app.Run();

internal record WeatherForecast(DateOnly Date, int TemperatureC, string? Summary)
{
	public int TemperatureF => 32 + (int)(TemperatureC / 0.5556);
}