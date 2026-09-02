var builder = WebApplication.CreateBuilder(args);
builder.Services.AddHealthChecks();

var app = builder.Build();

app.MapGet("/", (IConfiguration configuration) => Results.Ok(new
{
    service = "sample-api",
    message = configuration["MODULE_MESSAGE"]
}));
app.MapHealthChecks("/health");

app.Run();
