var builder = DistributedApplication.CreateBuilder(args);

// Azure OpenAI, provisioned by Aspire on first run (settings in appsettings.json + user secrets). Keyless: Entra ID only.
var openai = builder.AddAzureOpenAI("openai");

// Named by role, not by model, so swapping the model doesn't touch the apps.
openai.AddDeployment(
    name: "chat",
    modelName: "gpt-5-mini",
    modelVersion: "2025-08-07")
    .WithProperties(d => d.SkuName = "GlobalStandard"); // Aspire defaults to Standard, which has no gpt-5-mini quota in eastus2

// Runs the intake workflow. The only app with access to the models (least privilege).
var apiService = builder.AddProject<Projects.TicketIntake_ApiService>("apiservice")
    .WithHttpHealthCheck("/health")
    .WithReference(openai)
    .WaitFor(openai);

// Upload and reviews pages. Talks to apiservice only; no AI credentials.
builder.AddProject<Projects.TicketIntake_Web>("webfrontend")
    .WithExternalHttpEndpoints()
    .WithHttpHealthCheck("/health")
    .WithReference(apiService)
    .WaitFor(apiService);

builder.Build().Run();
