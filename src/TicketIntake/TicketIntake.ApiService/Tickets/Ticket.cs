using System.ComponentModel;

namespace TicketIntake.ApiService.Tickets;

// What one handwritten ticket says. Every extractor returns this shape, so results compare field by field.
// Null means "blank or unreadable"; a null field is a reason for human review, never a guess.
// [Description]s go into the JSON schema the model must follow, so they act as per-field instructions.
public sealed record Ticket(
    [property: Description("Pre-printed ticket number, top right")] string? TicketNumber,
    [property: Description("Date and time written on the ticket, as local time")] DateTime? PickupTime,
    string? DriverName,
    [property: Description("Site code, e.g. SITE-042")] string? SiteCode,
    string? TankNumber,
    [property: Description("Net volume in barrels")] decimal? VolumeBarrels,
    [property: Description("Temperature in degrees Fahrenheit")] decimal? TemperatureF,
    [property: Description("True if the driver signature box has a signature")] bool DriverSigned);
