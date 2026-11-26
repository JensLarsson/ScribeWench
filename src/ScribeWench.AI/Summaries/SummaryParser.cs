using ScribeWench.Core.Models;

namespace ScribeWench.AI.Summaries;

public static class SummaryParser
{
    // Replies that mean "no session note", by the marker the prompt asks for.
    private static readonly (string Marker, RecordingKind Kind)[] NotASessionMarkers =
    [
        ("NOT_A_SESSION:", RecordingKind.Neither),
        ("CAMPAIGN_DISCUSSION:", RecordingKind.Discussion),
    ];

    // Missing sections come out empty.
    public static SummaryResult Parse(string markdown)
    {
        var trimmed = markdown.TrimStart();
        foreach (var (marker, kind) in NotASessionMarkers)
        {
            if (!trimmed.StartsWith(marker, StringComparison.OrdinalIgnoreCase)) continue;

            var rest = trimmed[marker.Length..];
            var lineEnd = rest.IndexOf('\n');
            var reason = (lineEnd < 0 ? rest : rest[..lineEnd]).Trim();
            // A discussion goes on after the marker's line: its note, then the cast and vault updates.
            // For anything else only that line counts, whatever the model added after it.
            var after = kind == RecordingKind.Discussion && lineEnd >= 0 ? rest[(lineEnd + 1)..].Trim() : "";
            return new SummaryResult(after, [], [], [], "", kind) { Reason = reason };
        }

        var cliffhanger = ExtractSection(markdown, "## Cliffhanger",
            nextHeaders: ["## Loot", "## Key", "## NPC", "## Player", "## Cast Updates", "## Vault Updates"]);
        return new SummaryResult(
            markdown,
            ExtractBullets(markdown, "## Key Events"),
            ExtractBullets(markdown, "## NPCs Encountered"),
            ExtractBullets(markdown, "## Player Actions & Decisions"),
            cliffhanger.Trim());
    }

    private static string[] ExtractBullets(string markdown, string header)
    {
        var section = ExtractSection(markdown, header);
        if (string.IsNullOrWhiteSpace(section)) return [];

        return section
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(l => l.TrimStart().StartsWith('-') || l.TrimStart().StartsWith('*'))
            .Select(l => l.TrimStart('-', '*', ' ').Trim())
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .ToArray();
    }

    private static string ExtractSection(string markdown, string header, string[]? nextHeaders = null)
    {
        nextHeaders ??= ["## What", "## Key", "## NPC", "## Player", "## Cliffhanger", "## Loot"];

        var start = markdown.IndexOf(header, StringComparison.OrdinalIgnoreCase);
        if (start < 0) return string.Empty;

        start += header.Length;
        var end = markdown.Length;
        foreach (var next in nextHeaders)
        {
            if (next.Equals(header, StringComparison.OrdinalIgnoreCase)) continue;
            var idx = markdown.IndexOf(next, start, StringComparison.OrdinalIgnoreCase);
            if (idx > start && idx < end) end = idx;
        }
        return markdown[start..end].Trim();
    }
}
