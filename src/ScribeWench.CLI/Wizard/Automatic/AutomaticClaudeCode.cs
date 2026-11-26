using ScribeWench.CLI.Wizard.Environments;
using ScribeWench.Core.Models;
using Spectre.Console;

namespace ScribeWench.CLI.Wizard.Automatic;

// Claude Code, where it is on this PC and signed in.
public sealed class AutomaticClaudeCode : IAutomaticWriter
{
    // A subscription that is already here beats a download; a login that costs per session doesn't.
    public (int Order, string Label)? Offer(Findings found) => found.Claude switch
    {
        null => null,
        { UsesSubscription: true } claude =>
            (0, $"Claude, through Claude Code on this PC   [dim]your Claude subscription{(claude.Email is { } email ? $" ({Markup.Escape(email)})" : "")}; the best notes in our tests[/]"),
        _ => (2, "Claude, through Claude Code on this PC   [dim]its login here is billed per use[/]"),
    };

    public string? Notice(Findings found) => found.ClaudeSignedOut
        ? "[dim]Claude Code is on this PC but not signed in. Run `claude` and sign in, and it becomes a choice here.[/]"
        : null;

    public Task<AutomaticPlan?> PlanAsync(AppConfig config, Findings found, MachineInfo machine, bool canGoBack, CancellationToken ct) =>
        Task.FromResult<AutomaticPlan?>(new AutomaticPlan(
            "Claude opus, through Claude Code on this PC",
            "[dim]nothing to download[/]",
            _ =>
            {
                config.Ai.ClaudeCode.Model = "opus";
                config.Ai.Backend = "claudecode";
                return Task.CompletedTask;
            }));
}
