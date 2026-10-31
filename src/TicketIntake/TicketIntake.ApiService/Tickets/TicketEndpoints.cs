namespace TicketIntake.ApiService.Tickets;

// HTTP endpoints for intake and human review. The Web app calls these; it holds no AI or storage credentials.
public static class TicketEndpoints
{
    public static void MapTicketEndpoints(this WebApplication app)
    {
        // Upload a ticket photo and run the workflow; its last step stores the ticket (accepted, or waiting for review).
        app.MapPost("/tickets/intake", async (IFormFile image, IntakeWorkflow workflow, CancellationToken cancellationToken) =>
        {
            // Only images go to the models (untrusted input: check the type before spending tokens on it)
            if (!image.ContentType.StartsWith("image/"))
                return Results.BadRequest("Upload an image (PNG or JPEG).");

            using var buffer = new MemoryStream();
            await image.CopyToAsync(buffer, cancellationToken);
            TicketImage ticketImage = new(buffer.ToArray(), image.ContentType);

            return Results.Ok(await workflow.RunAsync(ticketImage, cancellationToken));
        })
        .DisableAntiforgery(); // called by the Web app and curl, not a browser form, so there's no antiforgery token

        // The review inbox.
        app.MapGet("/tickets/reviews", (TicketStore store) => store.NeedingReview());

        app.MapGet("/tickets/{id:guid}", (Guid id, TicketStore store) =>
            store.Get(id) is { } ticket ? Results.Ok(ticket) : Results.NotFound());

        // The original photo, so the reviewer reads the paper, not just our extraction.
        app.MapGet("/tickets/{id:guid}/image", (Guid id, TicketStore store) =>
            store.Get(id) is { } ticket ? Results.File(ticket.Image.Data, ticket.Image.MediaType) : Results.NotFound());

        // A reviewer's corrected values: every field filled, same business rules as the workflow, then saved as final.
        app.MapPost("/tickets/{id:guid}/review", (Guid id, Ticket corrected, TicketStore store) =>
        {
            if (store.Get(id) is null)
                return Results.NotFound();

            string[] problems =
            [
                .. TicketRules.Incomplete(corrected).Select(field => $"{field}: required"),
                .. TicketRules.Check(corrected).Select(p => $"{p.Field}: {p.Problem}")
            ];
            if (problems.Length > 0)
                return Results.BadRequest(problems);

            store.Accept(id, corrected);
            return Results.NoContent();
        });
    }
}
