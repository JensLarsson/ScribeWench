using ScribeWench.AI.Summaries;
using ScribeWench.CLI.Commands;
using ScribeWench.Core.Models;
using Spectre.Console;

namespace ScribeWench.CLI.Summarize;

public sealed record LoadedSession(IReadOnlyList<TranscriptSegment> Segments, SessionDetails Details);

// Session is null when there is nothing to summarize; the exit code then says whether that is a failure.
public sealed record SourceResult(LoadedSession? Session, int ExitCode = 0);

/// <summary>
/// Strategy for what `summarize` is given: a recording to transcribe, or a transcript saved earlier.
/// Each says on the console what it found.
/// </summary>
public interface ISessionSource
{
    bool Reads(string path);

    Task<SourceResult> ReadAsync(string path, SummarizeCommandSettings settings);
}

public static class TranscriptTable
{
    public static void Write(IAnsiConsole console, IEnumerable<TranscriptSegment> segments)
    {
        var table = new Table().Border(TableBorder.Rounded)
            .AddColumn("[dim]Time[/]")
            .AddColumn("Text");
        foreach (var seg in segments)
            table.AddRow($"[dim]{seg.Start:mm\\:ss} → {seg.End:mm\\:ss}[/]", Markup.Escape(seg.Text));
        console.Write(table);
    }
}
