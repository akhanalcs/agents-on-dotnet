using System.Collections.Concurrent;
using System.Text.Json.Serialization;

namespace TicketIntake.ApiService.Tickets;

// One processed ticket. Keeps what the machines read (Intake) apart from the values we keep (Final), for audit.
// Final is null until the ticket is accepted: automatically when nothing needs review, otherwise by a reviewer.
public sealed record StoredTicket(
    Guid Id,
    DateTimeOffset ReceivedAt,
    [property: JsonIgnore] TicketImage Image, // served by its own endpoint, not inside the JSON
    IntakeResult Intake,
    Ticket? Final)
{
    public bool NeedsReview => Final is null;
}

// Placeholder storage: in memory, lost on restart. Later: the database for values, Blob Storage for images.
public sealed class TicketStore
{
    private readonly ConcurrentDictionary<Guid, StoredTicket> _tickets = new();

    public StoredTicket Add(TicketImage image, IntakeResult intake)
    {
        Ticket? final = intake.NeedsReview.Count == 0 ? intake.Ticket : null; // nothing to check: accepted straight away
        StoredTicket ticket = new(Guid.NewGuid(), DateTimeOffset.UtcNow, image, intake, final);
        _tickets[ticket.Id] = ticket;
        return ticket;
    }

    public StoredTicket? Get(Guid id) => _tickets.GetValueOrDefault(id);

    // The review inbox, oldest first.
    public IEnumerable<StoredTicket> NeedingReview() => _tickets.Values.Where(t => t.NeedsReview).OrderBy(t => t.ReceivedAt);

    public void Accept(Guid id, Ticket final) => _tickets[id] = _tickets[id] with { Final = final };
}
