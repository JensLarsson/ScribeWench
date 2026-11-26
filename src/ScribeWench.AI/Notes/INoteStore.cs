using Microsoft.Extensions.DependencyInjection;
using ScribeWench.AI.Backends;
using ScribeWench.AI.Summaries;
using ScribeWench.Core.Models;

namespace ScribeWench.AI.Notes;

// What create-game finds or makes for a game.
public sealed record GameFolder(string Path, bool Existed, int NoteCount);

/// <summary>
/// Strategy for where the notes are kept: a notes folder with a folder per game (an Obsidian vault,
/// or just Markdown files), or with none set up, only a folder for the summaries. Shared code never
/// asks which it has.
/// </summary>
public interface INoteStore
{
    /// <summary>Whether a session has to belong to a game, because the game decides whose notes it links to.</summary>
    bool NeedsGame { get; }

    /// <summary>Whether there are notes for `edit` to change.</summary>
    bool CanEdit { get; }

    /// <summary>Where a game's transcripts are saved.</summary>
    string TranscriptFolder(string? gameFolder);

    /// <summary>The cast's names for the transcriber to spell by; null without any.</summary>
    string? CastHint(string? gameFolder);

    /// <summary>Makes a new game's folder ready; null where games have none.</summary>
    GameFolder? PrepareGame(string gameFolder);

    /// <summary>The note a session gets first, whether or not it exists; null where sessions get none.</summary>
    string? SessionNotePath(SessionDetails session);

    /// <summary>What the model is told besides the transcript.</summary>
    SummaryContext BuildContext(
        SummarizationOptions options, InstructionsStore.Loaded instructions, SessionDetails session,
        string? replacingNotePath, string? defaultPromptPath);

    /// <summary>Saves a session's note, and what the session added to the cast and the other notes.</summary>
    Task<SavedSession> SaveAsync(SessionDraft draft, SessionDetails session, string aiLabel, string? replacePath, CancellationToken ct);

    /// <summary>Changes the notes to match an instruction given in plain words.</summary>
    Task<NoteEdits.Outcome> EditAsync(NoteEdits.Request request, IBackend backend, CancellationToken ct);
}

public static class NoteStoreServiceCollectionExtensions
{
    // A notes folder when one is set up; without one, summaries only.
    public static IServiceCollection AddNoteStore(this IServiceCollection services)
    {
        services.AddSingleton<ObsidianNoteStore>();
        services.AddSingleton<PlainNoteStore>();
        return services.AddSingleton<INoteStore>(sp =>
            string.IsNullOrWhiteSpace(sp.GetRequiredService<ObsidianConfig>().VaultPath)
                ? sp.GetRequiredService<PlainNoteStore>()
                : sp.GetRequiredService<ObsidianNoteStore>());
    }
}
