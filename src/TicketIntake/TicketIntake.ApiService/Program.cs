using Azure.AI.DocumentIntelligence;
using Azure.Identity;
using Microsoft.Extensions.AI;
using TicketIntake.ApiService.Tickets;

var builder = WebApplication.CreateBuilder(args);

// Add service defaults & Aspire client integrations.
builder.AddServiceDefaults();

// Keyless auth everywhere via DefaultAzureCredential: your az/azd sign-in locally, the app's managed identity in Azure.
// Locally, launchSettings.json sets AZURE_TOKEN_CREDENTIALS=dev, so it skips the managed identity probe (it can hang on a laptop).

// Azure OpenAI from the AppHost's "openai" connection.
// Registers IChatClient for the "chat" deployment. Aspire already adds OpenTelemetry (one span per model call, token metrics).
builder.AddAzureOpenAIClient("openai", settings =>
        settings.EnableSensitiveTelemetryData = builder.Environment.IsDevelopment()) // prompts/responses in traces: dev only
    .AddChatClient("chat");

// Document Intelligence (no Aspire client integration, so registered by hand).
builder.Services.AddSingleton(_ => new DocumentIntelligenceClient(
    new Uri(builder.Configuration.GetConnectionString("docintel") ?? throw new InvalidOperationException("Missing ConnectionStrings:docintel")),
    new DefaultAzureCredential()));

builder.Services.AddSingleton<TicketExtractor>();   // extractor A: LLM vision agent
builder.Services.AddSingleton<DocIntelExtractor>(); // extractor B: Document Intelligence
builder.Services.AddSingleton<IntakeWorkflow>(); // the graph is built once; each request is its own run
builder.Services.AddSingleton<TicketStore>();    // placeholder: in memory until the database step

// Add services to the container.
builder.Services.AddProblemDetails();

// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

var app = builder.Build();

// Configure the HTTP request pipeline.
app.UseExceptionHandler();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

string[] summaries = ["Freezing", "Bracing", "Chilly", "Cool", "Mild", "Warm", "Balmy", "Hot", "Sweltering", "Scorching"];

app.MapGet("/", () => "API service is running. Navigate to /weatherforecast to see sample data.");

// Smoke test: proves provisioning + keyless auth work.
app.MapGet("/model-check", async (IChatClient chat) =>
    (await chat.GetResponseAsync("Reply with exactly: model is reachable")).Text);

app.MapTicketEndpoints(); // intake + human review (Tickets/TicketEndpoints.cs)

app.MapGet("/weatherforecast", () =>
{
    var forecast = Enumerable.Range(1, 5).Select(index =>
        new WeatherForecast
        (
            DateOnly.FromDateTime(DateTime.Now.AddDays(index)),
            Random.Shared.Next(-20, 55),
            summaries[Random.Shared.Next(summaries.Length)]
        ))
        .ToArray();
    return forecast;
})
.WithName("GetWeatherForecast");

app.MapDefaultEndpoints();

app.Run();

record WeatherForecast(DateOnly Date, int TemperatureC, string? Summary)
{
    public int TemperatureF => 32 + (int)(TemperatureC / 0.5556);
}
