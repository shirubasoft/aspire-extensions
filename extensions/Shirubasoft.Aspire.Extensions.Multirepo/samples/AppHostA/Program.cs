using Aspire.Hosting;
using ModularSample.ModuleContract;

var builder = DistributedApplication.CreateBuilder(args);

var exported = AppHostAModule.Register(builder, builder.AppHostDirectory);
var module = builder.AddAppHostAModule(exported);

_ = module.Api;
_ = module.Project;
_ = module.CSharpApp;
_ = module.Static;
_ = module.GeneratedStatic;
_ = module.Executable;
_ = module.DotnetTool;
_ = module.Message;
_ = module.ConnectionString;
_ = module.ExternalService;
_ = module.ContainerRegistry;
_ = module.Custom;

await builder.Build().RunAsync();
