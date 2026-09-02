using Aspire.Hosting;
using Spire.ModuleContract;

var builder = DistributedApplication.CreateBuilder(args);

builder.ImportSpireModule();
await builder.Build().RunAsync();
