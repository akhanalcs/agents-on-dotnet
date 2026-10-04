var builder = DistributedApplication.CreateBuilder(args);

var apiService = builder.AddProject<Projects.TicketIntake_ApiService>("apiservice")
    .WithHttpHealthCheck("/health");

builder.AddProject<Projects.TicketIntake_Web>("webfrontend")
    .WithExternalHttpEndpoints()
    .WithHttpHealthCheck("/health")
    .WithReference(apiService)
    .WaitFor(apiService);

builder.Build().Run();
