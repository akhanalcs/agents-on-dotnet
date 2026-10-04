using Aspire.Hosting.Azure;
using Azure.Provisioning;
using Azure.Provisioning.CognitiveServices;
using Azure.Provisioning.Expressions;

var builder = DistributedApplication.CreateBuilder(args);

// Azure OpenAI, provisioned by Aspire on first run (settings in appsettings.json + user secrets). Keyless: Entra ID only.
var openai = builder.AddAzureOpenAI("openai");

// Named by role, not by model, so swapping the model doesn't touch the apps.
openai.AddDeployment(
    name: "chat",
    modelName: "gpt-5-mini",
    modelVersion: "2025-08-07")
    .WithProperties(d => d.SkuName = "GlobalStandard"); // Aspire defaults to Standard, which has no gpt-5-mini quota in eastus2

// Azure AI Document Intelligence: extractor B (OCR + form fields with per-field confidence).
// Aspire has no built-in integration, so we describe the resource ourselves (Azure.Provisioning -> Bicep).
var docIntel = builder.AddAzureInfrastructure("docintel", infra =>
{
    var account = new CognitiveServicesAccount("docintel")
    {
        Kind = "FormRecognizer", // the resource kind behind Document Intelligence
        Sku = new CognitiveServicesSku { Name = "S0" }, // standard tier; the free tier (F0) has tight page and rate limits
        Properties = new CognitiveServicesAccountProperties
        {
            CustomSubDomainName = BicepFunction.Interpolate($"docintel-{BicepFunction.GetUniqueString(BicepFunction.GetResourceGroup().Id)}"),
            DisableLocalAuth = true, // no keys: Entra ID only, same as OpenAI
            PublicNetworkAccess = ServiceAccountPublicNetworkAccess.Enabled
        }
    };
    infra.Add(account);

    // Aspire fills these in with whoever runs the app: you locally, the app's managed identity in Azure
    var principalId = new ProvisioningParameter(AzureBicepResource.KnownParameters.PrincipalId, typeof(string));
    var principalType = new ProvisioningParameter(AzureBicepResource.KnownParameters.PrincipalType, typeof(string));
    infra.Add(principalId);
    infra.Add(principalType);
    infra.Add(account.CreateRoleAssignment(CognitiveServicesBuiltInRole.CognitiveServicesUser, principalType, principalId));

    infra.Add(new ProvisioningOutput("endpoint", typeof(string)) { Value = account.Properties.Endpoint });
});

// Runs the intake workflow. The only app with access to the models (least privilege).
var apiService = builder.AddProject<Projects.TicketIntake_ApiService>("apiservice")
    .WithHttpHealthCheck("/health")
    .WithReference(openai)
    .WaitFor(openai)
    .WithEnvironment("ConnectionStrings__docintel", docIntel.GetOutput("endpoint")) // read as ConnectionStrings:docintel
    .WaitFor(docIntel);

// Upload and reviews pages. Talks to apiservice only; no AI credentials.
builder.AddProject<Projects.TicketIntake_Web>("webfrontend")
    .WithExternalHttpEndpoints()
    .WithHttpHealthCheck("/health")
    .WithReference(apiService)
    .WaitFor(apiService);

builder.Build().Run();
