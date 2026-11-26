using ScribeWench.AI.Backends;
using ScribeWench.CLI.Wizard.Environments;
using ScribeWench.Core.Models;

namespace ScribeWench.CLI.Wizard.Automatic;

// What setup found on this PC that its suggestions rest on.
// Claude: set when Claude Code is on this PC and signed in.
// Models: the tested local models this PC has the memory for, best first.
public sealed record Findings(
    string WhisperModel,
    GraphicsCard? WhisperCard,
    bool WhisperDownloaded,
    ClaudeAccount? Claude,
    bool ClaudeSignedOut,
    IReadOnlyList<LocalModel> Models);

// What "Do it for me" will do about the notes: its row in the plan, and the doing.
public sealed record AutomaticPlan(string What, string Todo, Func<CancellationToken, Task> ApplyAsync)
{
    // Why a better choice was passed over; or, with Possible false, why it can't be done at all.
    public string? Remark { get; init; }

    public bool Possible { get; init; } = true;

    public static AutomaticPlan Impossible(string why) => new("", "", _ => Task.CompletedTask) { Remark = why, Possible = false };
}

/// <summary>
/// Strategy for one answer to "who should write the session notes?" that "Do it for me" can carry
/// out without further questions.
/// </summary>
public interface IAutomaticWriter
{
    /// <summary>Its entry in the list and where it goes, lowest first; null when this PC can't offer it.</summary>
    (int Order, string Label)? Offer(Findings found);

    /// <summary>Something to say before the list, such as why it isn't on it.</summary>
    string? Notice(Findings found);

    /// <summary>Its own questions, or a look at what it needs. Null when the user went back to the list.</summary>
    Task<AutomaticPlan?> PlanAsync(AppConfig config, Findings found, MachineInfo machine, bool canGoBack, CancellationToken ct);
}
