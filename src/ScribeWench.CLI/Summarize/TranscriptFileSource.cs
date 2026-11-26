using ScribeWench.AI.Summaries;
using ScribeWench.CLI.Commands;
using ScribeWench.Core.Models;
using Spectre.Console;

namespace ScribeWench.CLI.Summarize;

// A transcript the bot or an earlier run saved: nothing to transcribe, and its title line names the game.
public sealed class TranscriptFileSource(GameResolver games, OutputConfig output, TimeProvider time, IAnsiConsole console) : ISessionSource
{
    public bool Reads(string path) => Path.GetExtension(path).Equals(".md", StringComparison.OrdinalIgnoreCase);

    public async Task<SourceResult> ReadAsync(string path, SummarizeCommandSettings settings)
    {
        // The transcript.
        var (segments, parsedCampaign, parsedStart) = TranscriptDocument.Parse(await File.ReadAllTextAsync(path));
        if (segments.Count == 0)
        {
            console.MarkupLine("[red]Could not parse any transcript segments from the file.[/]");
            return new SourceResult(null, ExitCode: 1);
        }
        console.MarkupLine($"[green]Loaded transcript:[/] {segments.Count} segments.");
        TranscriptTable.Write(console, segments);
        if (settings.NoSummary) return new SourceResult(null);

        // The game, and what the session's files are called.
        var (game, ok) = await games.ResolveAsync(settings.Game, parsedCampaign);
        if (!ok) return new SourceResult(null, ExitCode: 1);

        var campaign = game?.Name ?? parsedCampaign ?? output.CampaignName;
        var startedAt = parsedStart ?? time.GetLocalNow().DateTime;
        var stem = Path.GetFileNameWithoutExtension(path);
        var baseName = stem.EndsWith("_transcript", StringComparison.OrdinalIgnoreCase)
            ? stem[..^"_transcript".Length]
            : SessionDetails.BaseNameFor(campaign, startedAt);
        return new SourceResult(new LoadedSession(segments, new SessionDetails(campaign, startedAt, game?.Folder, baseName)));
    }
}
