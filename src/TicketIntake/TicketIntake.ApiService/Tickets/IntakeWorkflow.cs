using Microsoft.Agents.AI.Workflows;

namespace TicketIntake.ApiService.Tickets;

// Workflow input: one uploaded ticket photo.
public sealed record TicketImage(byte[] Data, string MediaType);

// What compare and validate pass along: the agreed values (null where disputed), plus the fields a person must check.
public sealed record IntakeResult(Ticket Ticket, IReadOnlyList<FieldReview> NeedsReview);

// One field a person must check: why, and what each extractor read (so the reviewer sees both).
public sealed record FieldReview(string Field, string Reason, object? Llm, object? DocIntel, float? DocIntelConfidence);

// The intake workflow: image → both extractors in parallel (fan-out) → compare when both are done (fan-in) → validate → save → output.
// Built once at startup; many tickets can run through it at the same time.
public sealed class IntakeWorkflow
{
    private readonly Workflow _workflow;

    public IntakeWorkflow(TicketExtractor llmExtractor, DocIntelExtractor docIntelExtractor, TicketStore store)
    {
        StartExecutor start = new();
        ValidateExecutor validate = new();
        SaveExecutor save = new(store);
        LlmExtractExecutor extractLlm = new(llmExtractor);
        DocIntelExtractExecutor extractDocIntel = new(docIntelExtractor);

        // Compare keeps the two readings in fields until both arrive, so it's stateful:
        // registered as a factory, so every run gets its own instance (like a scoped service).
        Func<string, string, ValueTask<CompareExecutor>> newCompare = (id, _) => ValueTask.FromResult(new CompareExecutor(id));
        ExecutorBinding compare = newCompare.BindExecutor("compare");

        _workflow = new WorkflowBuilder(start)
            .AddFanOutEdge(start, [extractLlm, extractDocIntel])          // the same image goes to both extractors; they run in parallel
            .AddFanInBarrierEdge([extractLlm, extractDocIntel], compare)  // compare waits for both readings
            .AddEdge(compare, validate)                                   // the agreed values go through the business rules
            .AddEdge(validate, save)                                      // stored as accepted, or waiting for review
            .WithOutputFrom(save)                                         // save's StoredTicket becomes a WorkflowOutputEvent
            .Build();
    }

    // Runs one ticket through the workflow and returns what was stored (or throws if a step failed).
    public async Task<StoredTicket> RunAsync(TicketImage image, CancellationToken cancellationToken = default)
    {
        // Concurrent: many runs share this one workflow definition; each run keeps its own state.
        await using Run run = await InProcessExecution.Concurrent.RunAsync(_workflow, image, cancellationToken: cancellationToken);

        foreach (WorkflowEvent evt in run.NewEvents)
        {
            switch (evt)
            {
                case WorkflowOutputEvent output when output.Is(out StoredTicket? result):
                    return result;
                case ExecutorFailedEvent failed:
                    throw new InvalidOperationException($"Step '{failed.ExecutorId}' failed.", failed.Data);
                case WorkflowErrorEvent error:
                    throw new InvalidOperationException("Workflow failed.", error.Exception);
            }
        }

        throw new InvalidOperationException("Workflow finished without output.");
    }
}

// Entry point: keeps the image in this run's state (save needs it at the end) and passes it on,
// so the fan-out edge can send it to both extractors.
internal sealed class StartExecutor()
    : Executor<TicketImage, TicketImage>("start", declareCrossRunShareable: true)
{
    public const string StateScope = "ticket"; // shared scope: state is per executor unless executors share a scope name

    public override async ValueTask<TicketImage> HandleAsync(TicketImage image, IWorkflowContext context, CancellationToken cancellationToken = default)
    {
        await context.QueueStateUpdateAsync("image", image, StateScope, cancellationToken);
        return image;
    }
}

// Extractor A: the LLM vision agent.
// declareCrossRunShareable: no per-run fields, so concurrent runs can safely use this one instance.
internal sealed class LlmExtractExecutor(TicketExtractor extractor)
    : Executor<TicketImage, Ticket>("extract-llm", declareCrossRunShareable: true)
{
    public override async ValueTask<Ticket> HandleAsync(TicketImage image, IWorkflowContext context, CancellationToken cancellationToken = default) =>
        await extractor.ExtractAsync(image.Data, image.MediaType, cancellationToken);
}

// Extractor B: Document Intelligence.
internal sealed class DocIntelExtractExecutor(DocIntelExtractor extractor)
    : Executor<TicketImage, DocIntelReading>("extract-docintel", declareCrossRunShareable: true)
{
    public override async ValueTask<DocIntelReading> HandleAsync(TicketImage image, IWorkflowContext context, CancellationToken cancellationToken = default) =>
        await extractor.ExtractAsync(image.Data, cancellationToken);
}

// Fan-in: compares the two readings field by field. Plain C#, no LLM: deterministic, cheap, unit-testable.
// Rule: both extractors agree → accepted. Disagree, or neither could read it → a person checks it.
internal sealed class CompareExecutor(string id) : Executor(id)
{
    private Ticket? _llm;
    private DocIntelReading? _docIntel;

    // The two readings arrive as two messages of different types: one handler per type, each keeps its reading.
    // SendsMessage declares the type this executor sends on to the next step (validate).
    protected override ProtocolBuilder ConfigureProtocol(ProtocolBuilder protocolBuilder) =>
        protocolBuilder
            .ConfigureRoutes(routes => routes
                .AddHandler<Ticket>((ticket, _) => _llm = ticket)
                .AddHandler<DocIntelReading>((reading, _) => _docIntel = reading))
            .SendsMessage<IntakeResult>();

    // Called after this superstep's messages are delivered. The barrier edge delivers both at once.
    protected override ValueTask OnMessageDeliveryFinishedAsync(IWorkflowContext context, CancellationToken cancellationToken = default)
    {
        if (_llm is null || _docIntel is null)
            return default;

        Ticket llm = _llm;
        DocIntelReading docIntel = _docIntel;
        List<FieldReview> needsReview = [];

        // The agreed value, or null + a review entry when they disagree or neither could read it.
        T? Agree<T>(string field, T? fromLlm, T? fromDocIntel)
        {
            float? confidence = docIntel.Confidence.TryGetValue(field, out float c) ? c : null;

            if (fromLlm is null && fromDocIntel is null)
                needsReview.Add(new FieldReview(field, "unreadable", null, null, confidence));
            else if (!Same(fromLlm, fromDocIntel))
                needsReview.Add(new FieldReview(field, "extractors disagree", fromLlm, fromDocIntel, confidence));
            else
                return fromLlm;

            return default;
        }

        Ticket agreed = new(
            TicketNumber: Agree(nameof(Ticket.TicketNumber), llm.TicketNumber, docIntel.Ticket.TicketNumber),
            PickupTime: Agree(nameof(Ticket.PickupTime), llm.PickupTime, docIntel.Ticket.PickupTime),
            DriverName: Agree(nameof(Ticket.DriverName), llm.DriverName, docIntel.Ticket.DriverName),
            SiteCode: Agree(nameof(Ticket.SiteCode), llm.SiteCode, docIntel.Ticket.SiteCode),
            TankNumber: Agree(nameof(Ticket.TankNumber), llm.TankNumber, docIntel.Ticket.TankNumber),
            VolumeBarrels: Agree(nameof(Ticket.VolumeBarrels), llm.VolumeBarrels, docIntel.Ticket.VolumeBarrels),
            TemperatureF: Agree(nameof(Ticket.TemperatureF), llm.TemperatureF, docIntel.Ticket.TemperatureF),
            DriverSigned: llm.DriverSigned); // only the agent can see a signature (DI reads text)

        if (llm.DriverSigned is not true)
            needsReview.Add(new FieldReview(nameof(Ticket.DriverSigned), llm.DriverSigned is false ? "not signed" : "unreadable", llm.DriverSigned, null, null));

        return context.SendMessageAsync(new IntakeResult(agreed, needsReview), cancellationToken: cancellationToken);
    }

    // Text compares ignoring case and outer spaces ("j. martinez " = "J. Martinez"); numbers and dates by value (162.40 = 162.4).
    private static bool Same(object? a, object? b) =>
        a is string x && b is string y
            ? string.Equals(x.Trim(), y.Trim(), StringComparison.OrdinalIgnoreCase)
            : Equals(a, b);
}

// Business rules on the agreed values (ranges, reference data). Plain C#: the same rules check a reviewer's fix later.
internal sealed class ValidateExecutor()
    : Executor<IntakeResult, IntakeResult>("validate", declareCrossRunShareable: true)
{
    public override ValueTask<IntakeResult> HandleAsync(IntakeResult result, IWorkflowContext context, CancellationToken cancellationToken = default)
    {
        IEnumerable<FieldReview> broken = TicketRules.Check(result.Ticket)
            .Select(p => new FieldReview(p.Field, p.Problem, null, null, null));

        return ValueTask.FromResult(result with { NeedsReview = [.. result.NeedsReview, .. broken] });
    }
}

// Persist: stores the ticket as accepted (nothing to check) or waiting for review. The run's last step and its output.
internal sealed class SaveExecutor(TicketStore store)
    : Executor<IntakeResult, StoredTicket>("save", declareCrossRunShareable: true)
{
    public override async ValueTask<StoredTicket> HandleAsync(IntakeResult result, IWorkflowContext context, CancellationToken cancellationToken = default)
    {
        TicketImage image = await context.ReadStateAsync<TicketImage>("image", StartExecutor.StateScope, cancellationToken)
            ?? throw new InvalidOperationException("The ticket image is missing from the run's state.");
        return store.Add(image, result);
    }
}
