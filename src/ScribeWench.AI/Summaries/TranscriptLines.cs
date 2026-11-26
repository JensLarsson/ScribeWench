using ScribeWench.Core.Models;

namespace ScribeWench.AI.Summaries;

// A speaker's consecutive speech joined into one line, without the per-line timestamps, which cost
// a small model more tokens than short lines do.
public sealed record TranscriptLine(TimeSpan Start, TimeSpan End, string Speaker, string Text)
{
    // Characters this line takes in a request, line break included.
    public int Length => Speaker.Length + Text.Length + 3;

    public override string ToString() => $"{Speaker}: {Text}";
}

public static class TranscriptLines
{
    // So a transcript can always be divided between lines.
    public const int MaxLineCharacters = 1000;

    // The transcript in time order. A segment repeating what the same speaker said just before is
    // dropped: Whisper sometimes gets stuck on a line.
    public static List<TranscriptLine> Compact(IEnumerable<TranscriptSegment> segments)
    {
        var lines = new List<TranscriptLine>();
        var lastSaid = new Dictionary<string, string>(StringComparer.Ordinal);

        // A stable sort: equal starts keep their order.
        foreach (var segment in segments.OrderBy(s => s.Start))
        {
            var text = segment.Text.Trim();
            if (text.Length == 0) continue;
            if (lastSaid.TryGetValue(segment.SpeakerLabel, out var before) && before.Equals(text, StringComparison.OrdinalIgnoreCase))
                continue;
            lastSaid[segment.SpeakerLabel] = text;

            var end = segment.End > segment.Start ? segment.End : segment.Start;
            foreach (var piece in SplitLong(text))
            {
                if (lines.Count > 0 && lines[^1] is var last && last.Speaker == segment.SpeakerLabel
                    && last.Text.Length + piece.Length < MaxLineCharacters)
                    lines[^1] = last with { End = end, Text = last.Text + " " + piece };
                else
                    lines.Add(new TranscriptLine(segment.Start, end, segment.SpeakerLabel, piece));
            }
        }
        return lines;
    }

    public static string Format(IEnumerable<TranscriptLine> lines) => string.Join("\n", lines) + "\n";

    // Whisper segments are never this long; a pasted transcript might be.
    private static IEnumerable<string> SplitLong(string text)
    {
        while (text.Length > MaxLineCharacters)
        {
            var cut = text.LastIndexOf(' ', MaxLineCharacters - 1);
            if (cut <= 0) cut = MaxLineCharacters;
            yield return text[..cut].TrimEnd();
            text = text[cut..].TrimStart();
        }
        if (text.Length > 0) yield return text;
    }
}
