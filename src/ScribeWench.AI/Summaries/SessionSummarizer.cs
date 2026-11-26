using ScribeWench.AI.Backends;
using ScribeWench.AI.Notes;
using ScribeWench.Core.Models;

namespace ScribeWench.AI.Summaries;

// What every summary goes through, whether the session came from Discord or from a file: gather
// what the model is told, have it write, save. In three steps so the caller can say what is
// happening in between.
public sealed class SessionSummarizer(BackendRegistry backends, INoteStore notes, InstructionsStore instructions)
{
    // replacingNotePath: for a re-run, the session's current note, sent so its corrections can be kept.
    public SummaryContext Prepare(SessionDetails session, string? replacingNotePath = null)
    {
        var defaultPromptPath = instructions.EnsureDefaultFile();
        var loaded = instructions.Load(session.GameFolder);
        var options = new SummarizationOptions(session.Campaign, session.StartedAt)
        {
            SystemPrompt = Prompts.BuildSystemPrompt(loaded.Instructions),
        };
        return notes.BuildContext(options, loaded, session, replacingNotePath, defaultPromptPath);
    }

    public async Task<SessionDraft> WriteAsync(
        IReadOnlyList<TranscriptSegment> transcript, SummaryContext context, CancellationToken ct = default)
    {
        var summary = await backends.Current.SummarizeAsync(transcript, context.Options, ct);

        // The model ends its reply with what it learned about the cast and the notes. Neither belongs in the note.
        var (withoutCast, cast) = CastNote.SplitUpdates(summary.Markdown);
        var (markdown, vault) = VaultUpdates.Split(withoutCast);
        summary = summary with { Markdown = SessionDocuments.WithoutChanges(markdown) };
        return new SessionDraft(summary, context.Options, cast, vault);
    }

    // replacePath: for a re-run, the note to overwrite instead of adding a "(2)" beside it.
    public Task<SavedSession> SaveAsync(SessionDraft draft, SessionDetails session, string? replacePath = null, CancellationToken ct = default) =>
        notes.SaveAsync(draft, session, backends.Current.Label, replacePath, ct);

    // Ends with what the recording changed in the game's other notes.
    public string ForDiscord(SessionDraft draft, SavedSession saved) =>
        SessionDocuments.ForDiscord(draft.Summary, draft.Options, backends.Current.Label, SessionDocuments.ChangeLines(saved.Notes));
}
