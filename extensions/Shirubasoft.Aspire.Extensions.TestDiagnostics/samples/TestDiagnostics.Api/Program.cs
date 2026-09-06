using System.Diagnostics;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.AddOpenTelemetry(logging => logging.IncludeFormattedMessage = true);
builder.Services.AddOpenTelemetry()
    .WithTracing(tracing => tracing.AddSource("TestDiagnostics.Sample").SetSampler(new AlwaysOnSampler()))
    .UseOtlpExporter();
var app = builder.Build();
using var source = new ActivitySource("TestDiagnostics.Sample");
app.MapGet("/sample", (ILoggerFactory loggers, string? marker) =>
{
    using var activity = source.StartActivity("sample-request");
    activity?.SetTag("sample.marker", marker);
    loggers.CreateLogger("Sample").LogInformation("Sample diagnostic message {Marker}", marker);
    return "Diagnostics emitted";
});
await app.RunAsync();
