using Microsoft.Extensions.Logging;
using ScribeWench.AI.Backends;
using ScribeWench.AI.Summaries;
using ScribeWench.Core;
using ScribeWench.Core.Models;

namespace ScribeWench.AI.Notes;

// A notes folder with a folder per game: the game's notes are sent to the model as link targets,
// its session notes, discussion notes and transcripts are written there, and the cast and the other
// notes are kept up to date with what each recording establishes.
public sealed class ObsidianNoteStore(
    ObsidianConfig obsidian,
    OutputConfig output,
    ScribePaths paths,
    TimeProvider time,
    ILogger<ObsidianNoteStore> logger) : INoteStore
{
    // Sent with a re-run, after the notes: the session's current note, which players may have
    // corrected since it was written, and how to weigh it against the transcript.
    public const string ReplacingNoteInstructions = """
        THIS SESSION'S CURRENT NOTE: this session was summarized before, and the note below is what the vault holds for it now. Players may have corrected it by hand since it was written. Write the session note fresh from the transcript, then weigh it against this one passage by passage:
        - Where the current note says something differently on purpose (a correction, a clarification, something reworded as uncertain or as speculation) and that is consistent with the transcript, keep the current note's version, in your own words or theirs.
        - Where the current note leaves something out, is vaguer, or contradicts what the transcript clearly shows, follow the transcript.
        - When you can't tell which is right, prefer the current note: a person chose to write it.
        Its text is also what the vault already says about this session, so don't add facts to other notes only because they are in it.
        """;

    public bool NeedsGame => true;
    public bool CanEdit => true;

    private ObsidianVault Open(string? gameFolder) => ObsidianVault.Open(obsidian, gameFolder);

    public string TranscriptFolder(string? gameFolder) =>
        string.IsNullOrWhiteSpace(gameFolder) ? PlainNoteStore.FolderFor(output, gameFolder) : Open(gameFolder).TranscriptsPath;

    public string? CastHint(string? gameFolder) => CastNote.ToWhisperHint(CastNote.Read(Open(gameFolder).CastPath));

    public GameFolder? PrepareGame(string gameFolder)
    {
        // Notes started before the game was created are kept and used.
        var existed = Directory.Exists(Path.Combine(ObsidianVault.ResolveRoot(obsidian), gameFolder));
        var vault = Open(gameFolder);
        Directory.CreateDirectory(vault.SessionsPath);
        if (!File.Exists(vault.CastPath)) File.WriteAllText(vault.CastPath, CastNote.Template);
        return new GameFolder(vault.RootPath, existed, vault.ListNotes().Count);
    }

    public string? SessionNotePath(SessionDetails session) =>
        Open(session.GameFolder).SessionNotePath(new SummarizationOptions(session.Campaign, session.StartedAt));

    public SummaryContext BuildContext(
        SummarizationOptions options, InstructionsStore.Loaded instructions, SessionDetails session,
        string? replacingNotePath, string? defaultPromptPath)
    {
        // The notes to link to. Not the prompt file, should it live in the vault, and not the note
        // being replaced: that is sent below on its own, and the new note takes its name.
        var vault = Open(session.GameFolder);
        var notes = vault.ListNotes(new[] { defaultPromptPath, replacingNotePath }.OfType<string>());
        var previous = obsidian.IncludePreviousSession ? vault.FindPreviousSessionNote(session.StartedAt) : null;
        var replacing = replacingNotePath is not null && File.Exists(replacingNotePath);

        string BuildWith(int? maxNoteCharacters, out ObsidianVault.ContextStats built)
        {
            var text = vault.BuildContext(notes, previous, session.StartedAt, out built, instructions.Instructions, maxNoteCharacters);
            if (replacing)
                text = text.TrimEnd() + "\n\n" + ReplacingNoteInstructions.TrimEnd() + "\n" +
                       $"<note name=\"{Path.GetFileNameWithoutExtension(replacingNotePath)}\">\n" +
                       SessionDocuments.WithoutChanges(ObsidianVault.ReadBody(replacingNotePath!)).TrimEnd() + "\n</note>\n";
            return text;
        }

        var context = BuildWith(null, out var stats);
        if (replacing)
            logger.LogInformation("Re-run: weighing the current note {Path} against the transcript", replacingNotePath);

        // The same context with less note text in it, for a model with a small context window.
        // Whatever isn't note text (the cast, the note names, the rules for the reply) stays.
        string Within(int maxCharacters)
        {
            if (context.Length <= maxCharacters) return context;
            var fixedPart = context.Length - stats.Characters;
            return BuildWith(Math.Max(0, maxCharacters - fixedPart), out _);
        }

        logger.LogInformation(
            "Obsidian vault {Vault}: {Count} notes as link targets ({Full} sent in full, {Chars:N0} characters; {NameOnly} by name only), previous session {Previous}",
            vault.RootPath, notes.Count, stats.NotesInFull, stats.Characters, stats.NotesByNameOnly, previous ?? "none");
        if (obsidian.SendFullNotes && stats.NotesByNameOnly > 0 && stats.Characters >= obsidian.MaxNoteCharacters * 0.9)
            logger.LogWarning("The game's notes are larger than obsidian.maxNoteCharacters ({Max:N0}); the oldest were sent by name only.",
                obsidian.MaxNoteCharacters);

        return new SummaryContext(
            options with { Context = context, ContextWithin = Within, PartContext = vault.BuildPartContext(notes) },
            instructions.Path,
            new VaultLinks(notes.Count, previous));
    }

    public async Task<SavedSession> SaveAsync(SessionDraft draft, SessionDetails session, string aiLabel, string? replacePath, CancellationToken ct)
    {
        var vault = Open(session.GameFolder);
        var summary = draft.Summary;
        var discussion = summary.Kind == RecordingKind.Discussion;
        var replacing = summary.Kind == RecordingKind.Session && replacePath is not null && File.Exists(replacePath);
        // Every note this save overwrites or changes is copied here first.
        var backups = Path.Combine(paths.EditBackups, time.GetLocalNow().ToString("yyyyMMdd-HHmmss") + (replacing ? " re-run" : " session"));

        string Document(IReadOnlyList<string> changes) => discussion
            ? SessionDocuments.DiscussionNote(summary, draft.Options, aiLabel, changes)
            : SessionDocuments.ObsidianNote(summary, draft.Options, aiLabel, changes);

        // The cast note. The summary matters more: a failure here is logged and passed over.
        if (draft.CastUpdates.Count > 0)
        {
            try
            {
                CastNote.Append(vault.CastPath, draft.CastUpdates, draft.Options.SessionDateTime);
                logger.LogInformation("Cast note {Path}: added {Count} line(s)", vault.CastPath, draft.CastUpdates.Count);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not update the cast note");
            }
        }

        // The recording's own note: a session note, or a discussion note beside them. A re-run
        // overwrites the session's note after a backup, and keeps the changes that note lists.
        string? notePath = null;
        IReadOnlyList<string> carried = [];
        if (summary.Kind == RecordingKind.Neither)
        {
            logger.LogWarning("Not a game session; no note written to the vault: {Reason}", summary.Reason);
        }
        else if (replacing)
        {
            var backup = Path.Combine(backups, Path.GetRelativePath(vault.RootPath, replacePath!));
            Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
            File.Copy(replacePath!, backup, overwrite: true);
            carried = SessionDocuments.ChangeLinesIn(File.ReadAllText(replacePath!));
            await File.WriteAllTextAsync(replacePath!, Document(carried), ct);
            logger.LogInformation("Session note replaced: {Path} (previous version in {Backup})", replacePath, backup);
            notePath = replacePath;
        }
        else
        {
            notePath = discussion ? vault.NewDiscussionNotePath(draft.Options) : vault.NewSessionNotePath(draft.Options);
            Directory.CreateDirectory(Path.GetDirectoryName(notePath)!);
            await File.WriteAllTextAsync(notePath, Document([]), ct);
            logger.LogInformation("{Kind} note saved to {Path}", discussion ? "Discussion" : "Session", notePath);
        }

        // The other notes. What changed in them is listed at the end of the note above.
        var notes = UpdateNotes(vault, draft, notePath, backups);
        var changes = carried.Concat(SessionDocuments.ChangeLines(notes)).Distinct().ToList();
        if (notePath is not null && changes.Count > carried.Count)
            await File.WriteAllTextAsync(notePath, Document(changes), ct);

        return new SavedSession(notePath, notes);
    }

    // Brings the game's other notes up to date with what the recording established, each new line
    // linked to the recording's note. Never fails the session.
    private VaultUpdates.Result UpdateNotes(ObsidianVault vault, SessionDraft draft, string? sessionNotePath, string backups)
    {
        if (!obsidian.UpdateNotes) return VaultUpdates.Result.Empty;

        try
        {
            var renamed = VaultUpdates.RenameLegacyHeadings(vault);
            if (renamed.Count > 0)
                logger.LogInformation("Renamed \"Noted by ScribeWench\" to \"From the sessions\" in {Count} note(s): {Names}",
                    renamed.Count, string.Join(", ", renamed));
            if (draft.VaultUpdates.Count == 0) return VaultUpdates.Result.Empty;

            var label = sessionNotePath is null
                ? draft.Options.SessionDateTime.ToString("yyyy-MM-dd")
                : $"[[{Path.GetFileNameWithoutExtension(sessionNotePath)}]]";
            var result = VaultUpdates.Apply(vault, draft.VaultUpdates, label, backups);
            logger.LogInformation("Vault notes: {Created} created ({CreatedNames}), {Updated} updated ({UpdatedNames})",
                result.Created.Count, string.Join(", ", result.Created), result.Updated.Count, string.Join(", ", result.Updated));
            if (result.Changes.Count > 0)
                logger.LogInformation("Vault notes changed (as they were: {Backups}): {Changes}", backups, string.Join("; ", result.Changes));
            if (result.NotChanged.Count > 0)
                logger.LogWarning("Changes to the vault notes that couldn't be made: {Problems}", string.Join("; ", result.NotChanged));
            return result;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not update the vault notes");
            return VaultUpdates.Result.Empty;
        }
    }

    public async Task<NoteEdits.Outcome> EditAsync(NoteEdits.Request request, IBackend backend, CancellationToken ct)
    {
        var vault = Open(request.GameFolder);
        var attachments = EditAttachments.Prepare(vault, request.Files);
        var message = NoteEdits.BuildMessage(
            vault, request.GameName, request.Instruction, request.RequestedBy, obsidian.MaxNoteCharacters, attachments);
        var reply = await backend.CompleteAsync(NoteEdits.SystemPrompt, message, ct);
        return NoteEdits.Apply(vault, NoteEdits.Parse(reply), paths.EditBackups, time.GetLocalNow().DateTime, attachments);
    }
}
