using ScribeWench.AI.Notes;
using ScribeWench.AI.Summaries;
using ScribeWench.CLI.Commands;
using Spectre.Console;

namespace ScribeWench.CLI.Summarize;

// `scribewench summarize`: session notes from a recording or a saved transcript, the way the bot
// makes them after a session.
public sealed class SummarizeRunner(
    IEnumerable<ISessionSource> sources,
    AudioFileSource audio,
    SessionSummarizer summarizer,
    INoteStore notes,
    IAnsiConsole console)
{
    public async Task<int> RunAsync(SummarizeCommandSettings settings)
    {
        // The file.
        var path = settings.WavPath;
        if (path is null)
        {
            path = console.Prompt(new TextPrompt<string>("Path to audio file (WAV):").AllowEmpty());
            if (string.IsNullOrWhiteSpace(path))
            {
                console.MarkupLine("[red]No file specified.[/]");
                return 1;
            }
            path = path.Trim('"');
        }
        if (!File.Exists(path))
        {
            console.MarkupLine($"[red]File not found:[/] {Markup.Escape(path)}");
            return 1;
        }

        // The session in it.
        var source = sources.FirstOrDefault(s => s.Reads(path)) ?? audio;
        var (session, exitCode) = await source.ReadAsync(path, settings);
        if (session is null) return exitCode;
        if (settings.NoSummary) return 0;
        var details = session.Details;

        // For a re-run, the note to replace.
        string? replacePath = null;
        if (settings.Replace)
        {
            replacePath = notes.SessionNotePath(details);
            if (replacePath is null || !File.Exists(replacePath))
            {
                console.MarkupLine($"[red]--replace:[/] there's no note for this session to replace{(replacePath is null ? " (no notes folder is set up)" : $" ({Markup.Escape(replacePath)})")}.");
                return 1;
            }
            console.MarkupLine($"[dim]Replacing:[/] {Markup.Escape(replacePath)}");
        }

        // What the model is told.
        SummaryContext context;
        try
        {
            context = summarizer.Prepare(details, replacePath);
        }
        catch (DirectoryNotFoundException ex)
        {
            console.MarkupLine($"[red]{Markup.Escape(ex.Message)}[/]");
            return 1;
        }
        if (context.PromptPath is not null)
            console.MarkupLine($"[dim]Instructions:[/] {Markup.Escape(context.PromptPath)}");
        if (context.Vault is { } vault)
            console.MarkupLine($"[dim]Vault:[/] {vault.NoteCount} notes to link" +
                (vault.PreviousSessionPath is null ? "" : $", previous session [bold]{Markup.Escape(Path.GetFileNameWithoutExtension(vault.PreviousSessionPath))}[/]"));

        // The summary.
        console.WriteLine();
        var draft = await console.Status().Spinner(Spinner.Known.Dots)
            .StartAsync("Generating summary...", _ => summarizer.WriteAsync(session.Segments, context));
        console.WriteLine();
        console.Write(new Rule("[bold green]Summary[/]"));
        console.WriteLine(SessionDocuments.Body(draft.Summary));

        // Save it, and say what changed.
        var saved = await summarizer.SaveAsync(draft, details, replacePath);
        if (saved.NotePath is not null)
            console.MarkupLine($"[green]Summary saved:[/]  {Markup.Escape(saved.NotePath)}");
        foreach (var change in saved.Notes.Changes)
            console.MarkupLine($"[green]Changed:[/]  {Markup.Escape(change)}");
        foreach (var problem in saved.Notes.NotChanged)
            console.MarkupLine($"[yellow]Not changed:[/]  {Markup.Escape(problem)}");
        if (saved.Notes.Created.Count > 0)
            console.MarkupLine($"[green]New notes:[/]  {Markup.Escape(string.Join(", ", saved.Notes.Created))}");
        if (saved.Notes.Updated.Count > 0)
            console.MarkupLine($"[green]Updated notes:[/]  {Markup.Escape(string.Join(", ", saved.Notes.Updated))}");
        if (draft.Summary.NotASession && saved.NotePath is null && !saved.Notes.Any)
            console.MarkupLine("[yellow]Not a game session, so nothing was saved.[/]");
        return 0;
    }
}
