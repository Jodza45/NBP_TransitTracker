namespace TransitTracker.Application.DTOs;

public sealed class StreamEntryDto
{
    public required string Id { get; set; }
    public required IReadOnlyDictionary<string, string> Values { get; set; }
}
