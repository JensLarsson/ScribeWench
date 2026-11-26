using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ScribeWench.Core.Models;

namespace ScribeWench.AI.Summaries;

// The transcript as a Markdown file: a title line, then one line per segment.
public static class TranscriptDocument
{
    // "[start-end]" from the bot, "[start]" from older transcripts and from `summarize`.
    private static readonly Regex SegmentLine =
        new(@"^\[(\d{2}:\d{2}:\d{2})(?:-\d{2}:\d{2}:\d{2})?\] \*\*(.+?)\*\*: (.+)$", RegexOptions.Compiled);

    private static readonly Regex TitleLine =
        new(@"^# (.+) — Transcript — (\d{4}-\d{2}-\d{2} \d{2}:\d{2})$", RegexOptions.Compiled);

    // The game's name in the title line is what lets `scribewench summarize` send a re-run to the same game.
    public static string Build(IEnumerable<TranscriptSegment> segments, string campaign, DateTime startedAt, bool withEnds)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# {campaign} — Transcript — {startedAt:yyyy-MM-dd HH:mm}");
        sb.AppendLine();
        foreach (var seg in segments)
            sb.AppendLine(withEnds
                ? $"[{seg.Start:hh\\:mm\\:ss}-{seg.End:hh\\:mm\\:ss}] **{seg.SpeakerLabel}**: {seg.Text}"
                : $"[{seg.Start:hh\\:mm\\:ss}] **{seg.SpeakerLabel}**: {seg.Text}");
        return sb.ToString();
    }

    public static (List<TranscriptSegment> Segments, string? Campaign, DateTime? StartedAt) Parse(string text)
    {
        var segments = new List<TranscriptSegment>();
        string? campaign = null;
        DateTime? startedAt = null;

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');

            if (campaign is null && TitleLine.Match(line) is { Success: true } title)
            {
                campaign = title.Groups[1].Value;
                if (DateTime.TryParseExact(title.Groups[2].Value, "yyyy-MM-dd HH:mm", null, DateTimeStyles.None, out var parsed))
                    startedAt = parsed;
                continue;
            }

            var match = SegmentLine.Match(line);
            if (!match.Success) continue;

            var start = TimeSpan.Parse(match.Groups[1].Value);
            segments.Add(new TranscriptSegment(match.Groups[2].Value, 0, start, start, match.Groups[3].Value));
        }
        return (segments, campaign, startedAt);
    }
}
