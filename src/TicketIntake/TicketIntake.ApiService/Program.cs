using Azure;
using Azure.AI.DocumentIntelligence;
using Azure.Identity;
using Microsoft.Extensions.AI;
using TicketIntake.ApiService.Tickets;

var builder = WebApplication.CreateBuilder(args);

// Add service defaults & Aspire client integrations.
builder.AddServiceDefaults();

// Azure OpenAI from the AppHost's "openai" connection (keyless, DefaultAzureCredential).
// Registers IChatClient for the "chat" deployment. Aspire already adds OpenTelemetry (one span per model call, token metrics).
builder.AddAzureOpenAIClient("openai", settings =>
        settings.EnableSensitiveTelemetryData = builder.Environment.IsDevelopment()) // prompts/responses in traces: dev only
    .AddChatClient("chat");

// Document Intelligence (no Aspire client integration, so registered by hand). Entra ID, no keys.
builder.Services.AddSingleton(_ => new DocumentIntelligenceClient(
    new Uri(builder.Configuration.GetConnectionString("docintel") ?? throw new InvalidOperationException("Missing ConnectionStrings:docintel")),
    new DefaultAzureCredential()));

builder.Services.AddSingleton<TicketExtractor>();
builder.Services.AddSingleton<IntakeWorkflow>(); // the graph is built once; each request is its own run

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

// Step 2a check: what Document Intelligence reads on a ticket (key-value pairs + confidence), before we map it to a Ticket.
app.MapPost("/docintel-check", async (IFormFile image, DocumentIntelligenceClient docIntel, CancellationToken cancellationToken) =>
{
    using var buffer = new MemoryStream();
    await image.CopyToAsync(buffer, cancellationToken);

    // prebuilt-layout: OCR + layout, no training. The KeyValuePairs add-on pairs form labels with what's written next to them.
    AnalyzeDocumentOptions options = new("prebuilt-layout", BinaryData.FromBytes(buffer.ToArray()))
    {
        Features = { DocumentAnalysisFeature.KeyValuePairs }
    };
    Operation<AnalyzeResult> operation = await docIntel.AnalyzeDocumentAsync(WaitUntil.Completed, options, cancellationToken);

    return operation.Value.KeyValuePairs.Select(kv => new { Key = kv.Key.Content, Value = kv.Value?.Content, kv.Confidence });
})
.DisableAntiforgery();

// Upload a ticket photo, run it through the intake workflow, get the result as JSON.
app.MapPost("/tickets/intake", async (IFormFile image, IntakeWorkflow workflow, CancellationToken cancellationToken) =>
{
    // Only images go to the model (untrusted input: check the type before spending tokens on it)
    if (!image.ContentType.StartsWith("image/"))
        return Results.BadRequest("Upload an image (PNG or JPEG).");

    using var buffer = new MemoryStream();
    await image.CopyToAsync(buffer, cancellationToken);
    IntakeResult result = await workflow.RunAsync(new TicketImage(buffer.ToArray(), image.ContentType), cancellationToken);
    return Results.Ok(result);
})
.DisableAntiforgery(); // called by the Web app and curl, not a browser form, so there's no antiforgery token

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
