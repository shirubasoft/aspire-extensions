var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();
var slowStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
app.MapGet("/greeting", () => "Hello from Aspire");
app.MapGet("/health", () => Results.Ok());
app.MapGet("/slow-started", async (CancellationToken token) =>
{
    await slowStarted.Task.WaitAsync(token);
    return Results.Ok();
});
app.MapGet("/slow", async (CancellationToken token) =>
{
    slowStarted.TrySetResult();
    await Task.Delay(TimeSpan.FromSeconds(30), token);
    return Results.Ok();
});
await app.RunAsync();
