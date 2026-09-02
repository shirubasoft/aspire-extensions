using Aspire.Hosting;
using Spire.ModuleContract;

var builder = DistributedApplication.CreateBuilder(args);

builder.AddSpireModule();
await builder.Build().RunAsync();
