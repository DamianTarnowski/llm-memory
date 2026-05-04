var builder = DistributedApplication.CreateBuilder(args);

var memoryDb = builder.AddConnectionString("memorydb");

builder.AddProject<Projects.Memory_Api>("memory-api")
    .WithReference(memoryDb)
    .WithExternalHttpEndpoints();

builder.Build().Run();
