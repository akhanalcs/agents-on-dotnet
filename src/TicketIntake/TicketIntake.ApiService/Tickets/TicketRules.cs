namespace TicketIntake.ApiService.Tickets;

// Business rules on a ticket's values. Plain C#: the same rules run in the workflow and when a reviewer approves a fix.
public static class TicketRules
{
    // Placeholder reference data (moves to the database later): the tanks at each site.
    private static readonly Dictionary<string, string[]> TanksBySite = new(StringComparer.OrdinalIgnoreCase)
    {
        ["SITE-042"] = ["1", "2", "3"],
        ["SITE-017"] = ["1", "2", "3", "4"]
    };

    // One problem per rule the ticket breaks. Missing (null) values are skipped here; Incomplete reports those.
    public static IEnumerable<(string Field, string Problem)> Check(Ticket ticket)
    {
        if (ticket.VolumeBarrels is <= 0m or > 220m)
            yield return (nameof(Ticket.VolumeBarrels), "outside 0-220 bbl (one truckload)");

        if (ticket.TemperatureF is < -20m or > 140m)
            yield return (nameof(Ticket.TemperatureF), "outside -20 to 140 °F");

        if (ticket.PickupTime > DateTime.Now)
            yield return (nameof(Ticket.PickupTime), "in the future");

        if (ticket.SiteCode is null)
            yield break;

        if (!TanksBySite.TryGetValue(ticket.SiteCode, out string[]? tanks))
            yield return (nameof(Ticket.SiteCode), "unknown site");
        else if (ticket.TankNumber is not null && !tanks.Contains(ticket.TankNumber))
            yield return (nameof(Ticket.TankNumber), $"not a tank at {ticket.SiteCode}");
    }

    // Fields a person must still fill in before the ticket can be saved. A ticket must be signed to be accepted.
    public static IEnumerable<string> Incomplete(Ticket ticket)
    {
        if (ticket.TicketNumber is null) yield return nameof(Ticket.TicketNumber);
        if (ticket.PickupTime is null) yield return nameof(Ticket.PickupTime);
        if (ticket.DriverName is null) yield return nameof(Ticket.DriverName);
        if (ticket.SiteCode is null) yield return nameof(Ticket.SiteCode);
        if (ticket.TankNumber is null) yield return nameof(Ticket.TankNumber);
        if (ticket.VolumeBarrels is null) yield return nameof(Ticket.VolumeBarrels);
        if (ticket.TemperatureF is null) yield return nameof(Ticket.TemperatureF);
        if (ticket.DriverSigned is not true) yield return nameof(Ticket.DriverSigned);
    }
}
