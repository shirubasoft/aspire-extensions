var builder = DistributedApplication.CreateBuilder(args);

var tigerBeetle = builder.AddTigerBeetle("tigerbeetle")
    .WithDataVolume()
    .WithCacheGrid("256MiB")
    .PublishAsDockerComposeService((_, service) =>
    {
        service.SecurityOpt.Add("seccomp=unconfined");
    });

var rabbitMq = builder.AddRabbitMQ("rabbitmq")
    .WithManagementPlugin();

var client = builder.AddProject<Projects.TigerBeetle_Client>("client")
    .WithReference(tigerBeetle)
    .WithReference(rabbitMq)
    .WaitFor(tigerBeetle)
    .WaitFor(rabbitMq)
    .WithHttpEndpoint()
    .WithHttpHealthCheck("/health")
    .WithExternalHttpEndpoints();

tigerBeetle.AddChangeDataCapture("tigerbeetle-cdc", rabbitMq, "tigerbeetle")
    .WaitFor(client)
    .PublishAsDockerComposeService((_, service) =>
    {
        service.SecurityOpt.Add("seccomp=unconfined");
    });

builder.AddDockerComposeEnvironment("docker");

builder.Build().Run();
