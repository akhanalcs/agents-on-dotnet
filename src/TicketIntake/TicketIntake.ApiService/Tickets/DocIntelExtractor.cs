using System.Globalization;
using Azure;
using Azure.AI.DocumentIntelligence;

namespace TicketIntake.ApiService.Tickets;

// Extractor B's output: the same Ticket shape, plus Document Intelligence's confidence (0–1) for each field it found.
public sealed record DocIntelReading(Ticket Ticket, IReadOnlyDictionary<string, float> Confidence);

// Extractor B: Azure Document Intelligence (prebuilt-layout + key-value pairs, no training).
// OCR, not generative, so it fails differently from the LLM agent. Maps the form's printed labels to Ticket fields.
public sealed class DocIntelExtractor(DocumentIntelligenceClient client)
{
    public async Task<DocIntelReading> ExtractAsync(byte[] image, CancellationToken cancellationToken = default)
    {
        // The KeyValuePairs add-on pairs each printed label with what's written next to it
        AnalyzeDocumentOptions options = new("prebuilt-layout", BinaryData.FromBytes(image))
        {
            Features = { DocumentAnalysisFeature.KeyValuePairs }
        };
        Operation<AnalyzeResult> operation = await client.AnalyzeDocumentAsync(WaitUntil.Completed, options, cancellationToken);
        IReadOnlyList<DocumentKeyValuePair> pairs = operation.Value.KeyValuePairs;

        Dictionary<string, float> confidence = [];

        // Finds the value next to a label and records its confidence under the Ticket field name.
        // Prefix match, because labels are OCR'd too ("°F" came back as "ºF").
        string? Read(string label, string field)
        {
            DocumentKeyValuePair? pair = pairs.FirstOrDefault(p => p.Key.Content.StartsWith(label, StringComparison.OrdinalIgnoreCase));
            if (pair is null)
                return null;

            // PickupTime comes from two labels (Date + Time): keep the lower confidence
            confidence[field] = confidence.TryGetValue(field, out float existing) ? Math.Min(existing, pair.Confidence) : pair.Confidence;
            return pair.Value?.Content.Trim();
        }

        Ticket ticket = new(
            TicketNumber: Read("Ticket No", nameof(Ticket.TicketNumber)),
            PickupTime: ParseDateTime(Read("Date", nameof(Ticket.PickupTime)), Read("Time", nameof(Ticket.PickupTime))),
            DriverName: Read("Driver Name", nameof(Ticket.DriverName)),
            SiteCode: Read("Site Code", nameof(Ticket.SiteCode)),
            TankNumber: Read("Tank No", nameof(Ticket.TankNumber)),
            VolumeBarrels: ParseDecimal(Read("Net Volume", nameof(Ticket.VolumeBarrels))),
            TemperatureF: ParseDecimal(Read("Temperature", nameof(Ticket.TemperatureF))),
            DriverSigned: null); // a signature isn't text, so layout can't tell; the agent decides this one

        return new DocIntelReading(ticket, confidence);
    }

    // Text that doesn't parse becomes null ("unreadable"), same rule as the agent: never guess.
    private static decimal? ParseDecimal(string? text) =>
        decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal value) ? value : null;

    private static DateTime? ParseDateTime(string? date, string? time) =>
        DateTime.TryParseExact($"{date} {time}", "M/d/yyyy h:mm tt", CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out DateTime value)
            ? value
            : null;
}
