using Microsoft.Agents.AI.Workflows;

namespace TicketIntake.ApiService.Tickets;

// Workflow input: one uploaded ticket photo.
public sealed record TicketImage(byte[] Data, string MediaType);

// Workflow output: what was read, plus the fields a person must check. Empty list = ready to save.
public sealed record IntakeResult(Ticket Ticket, IReadOnlyList<string> NeedsReview);

// The intake workflow: image → extract (agent) → check (plain C#) → output.
// Built once at startup. Executors are stateless, so many tickets can run through it at the same time.
public sealed class IntakeWorkflow
{
    private readonly Workflow _workflow;

    public IntakeWorkflow(TicketExtractor extractor)
    {
        ExtractExecutor extract = new(extractor);
        CheckExecutor check = new();

        // The graph: Ticket returned by "extract" is the input message of "check"; "check"'s return value is the output.
        _workflow = new WorkflowBuilder(extract)
            .AddEdge(extract, check) // the Ticket that extract returns, becomes check's input message.
            .WithOutputFrom(check) // check's return value becomes a WorkflowOutputEvent.
            .Build();
    }

    // Runs one ticket through the workflow and returns its output (or throws if a step failed).
    public async Task<IntakeResult> RunAsync(TicketImage image, CancellationToken cancellationToken = default)
    {
        // Concurrent: many runs share this one workflow definition; each run keeps its own state.
        await using Run run = await InProcessExecution.Concurrent.RunAsync(_workflow, image, cancellationToken: cancellationToken);

        foreach (WorkflowEvent evt in run.NewEvents)
        {
            switch (evt)
            {
                case WorkflowOutputEvent output when output.Is(out IntakeResult? result):
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

// Step 1: the agent reads the photo into a Ticket.
// declareCrossRunShareable: no per-run fields, so concurrent runs can safely use this one instance.
internal sealed class ExtractExecutor(TicketExtractor extractor)
    : Executor<TicketImage, Ticket>("extract", declareCrossRunShareable: true)
{
    public override async ValueTask<Ticket> HandleAsync(TicketImage image, IWorkflowContext context, CancellationToken cancellationToken = default) =>
        await extractor.ExtractAsync(image.Data, image.MediaType, cancellationToken);
}

// Step 2: plain C#, no LLM. Rules are deterministic, cheap and unit-testable.
// Flags fields the extractor couldn't read (null) and a missing signature.
internal sealed class CheckExecutor()
    : Executor<Ticket, IntakeResult>("check", declareCrossRunShareable: true)
{
    public override ValueTask<IntakeResult> HandleAsync(Ticket ticket, IWorkflowContext context, CancellationToken cancellationToken = default)
    {
        (string Field, bool Failed)[] checks =
        [
            (nameof(Ticket.TicketNumber), ticket.TicketNumber is null),
            (nameof(Ticket.PickupTime), ticket.PickupTime is null),
            (nameof(Ticket.DriverName), ticket.DriverName is null),
            (nameof(Ticket.SiteCode), ticket.SiteCode is null),
            (nameof(Ticket.TankNumber), ticket.TankNumber is null),
            (nameof(Ticket.VolumeBarrels), ticket.VolumeBarrels is null),
            (nameof(Ticket.TemperatureF), ticket.TemperatureF is null),
            (nameof(Ticket.DriverSigned), !ticket.DriverSigned)
        ];

        string[] needsReview = checks.Where(c => c.Failed).Select(c => c.Field).ToArray();
        return ValueTask.FromResult(new IntakeResult(ticket, needsReview));
    }
}
