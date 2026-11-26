using ScribeWench.AI.Notes;
using ScribeWench.Core.Models;

namespace ScribeWench.AI.Summaries;

// Which session a summary is for. FileBaseName names its files where they aren't named by date and game.
public sealed record SessionDetails(string Campaign, DateTime StartedAt, string? GameFolder, string FileBaseName)
{
    public static string BaseNameFor(string campaign, DateTime startedAt) =>
        $"{string.Concat(campaign.Split(Path.GetInvalidFileNameChars())).Replace(' ', '_')}_{startedAt:yyyy-MM-dd_HH-mm}";
}

// What the game's notes gave the model to link to.
public sealed record VaultLinks(int NoteCount, string? PreviousSessionPath);

// Everything sent to the model besides the transcript. Built right before summarizing, so notes
// added during the session are in it. Vault is null without a notes folder.
public sealed record SummaryContext(SummarizationOptions Options, string? PromptPath, VaultLinks? Vault);

// The summary as the model wrote it, with what it reported about the cast and the other notes taken out.
public sealed record SessionDraft(
    SummaryResult Summary,
    SummarizationOptions Options,
    IReadOnlyList<string> CastUpdates,
    IReadOnlyList<VaultUpdates.Update> VaultUpdates);

// NotePath is null when nothing was saved.
public sealed record SavedSession(string? NotePath, VaultUpdates.Result Notes);
