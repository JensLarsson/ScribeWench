using Microsoft.Extensions.Logging;
using ScribeWench.AI.Backends;
using ScribeWench.AI.Summaries;
using ScribeWench.Core;
using ScribeWench.Core.Models;

namespace ScribeWench.AI.Notes;

// No notes folder: summaries and transcripts go to the output directory, and nothing is linked or kept up to date.
public sealed class PlainNoteStore(OutputConfig output, ILogger<PlainNoteStore> logger) : INoteStore
{
    public bool NeedsGame => false;
    public bool CanEdit => false;

    // The output directory, or the game's folder in it.
    public static string FolderFor(OutputConfig output, string? gameFolder)
    {
        var folder = PathHelper.Expand(output.Directory);
        return string.IsNullOrWhiteSpace(gameFolder) ? folder : Path.Combine(folder, gameFolder);
    }

    public string TranscriptFolder(string? gameFolder) => FolderFor(output, gameFolder);

    public string? CastHint(string? gameFolder) => null;

    public GameFolder? PrepareGame(string gameFolder) => null;

    public string? SessionNotePath(SessionDetails session) => null;

    public SummaryContext BuildContext(
        SummarizationOptions options, InstructionsStore.Loaded instructions, SessionDetails session,
        string? replacingNotePath, string? defaultPromptPath) =>
        new(options, instructions.Path, Vault: null);

    public async Task<SavedSession> SaveAsync(SessionDraft draft, SessionDetails session, string aiLabel, string? replacePath, CancellationToken ct)
    {
        if (!output.SaveMarkdown) return new SavedSession(null, VaultUpdates.Result.Empty);

        var folder = FolderFor(output, session.GameFolder);
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, $"{session.FileBaseName}.md");
        await File.WriteAllTextAsync(path, SessionDocuments.Plain(draft.Summary, draft.Options, aiLabel), ct);
        logger.LogInformation("Summary saved to {Path}", path);
        return new SavedSession(path, VaultUpdates.Result.Empty);
    }

    public Task<NoteEdits.Outcome> EditAsync(NoteEdits.Request request, IBackend backend, CancellationToken ct) =>
        throw new NotSupportedException("Editing needs the notes folder, which is turned off in this bot's setup.");
}
