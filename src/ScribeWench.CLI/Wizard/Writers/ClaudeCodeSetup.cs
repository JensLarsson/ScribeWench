using ScribeWench.CLI.Wizard.Environments;
using ScribeWench.Core.Models;
using Spectre.Console;

namespace ScribeWench.CLI.Wizard.Writers;

// Claude, through the Claude Code program on this PC.
public sealed class ClaudeCodeSetup(ISetupEnvironment environment, WizardUi ui, IAnsiConsole console) : IWriterSetup
{
    public string Backend => "claudecode";

    // First where it is installed, and near the end where it would have to be installed first.
    public int Order(WriterFindings found) => found.ClaudeCodeInstalled ? 0 : 30;

    public string Label(WriterFindings found) => found.ClaudeCodeInstalled
        ? "Claude, through Claude Code on this PC   [dim]your Claude subscription, no API key[/]"
        : "Claude, through Claude Code   [dim]your Claude subscription; not installed on this PC yet[/]";

    public string Describe(AiConfig ai) => $"Claude {ai.ClaudeCode.Model}, through Claude Code on this PC";

    public async Task<bool> ConfigureAsync(AiConfig ai, CancellationToken ct)
    {
        var claudeCode = ai.ClaudeCode;
        console.MarkupLine("[dim]Runs the Claude Code program on this PC with the account it is signed in to, and counts toward that[/]");
        console.MarkupLine("[dim]account's plan. Meant for your own games: to run one bot for other groups, use an Anthropic API key.[/]");

        // Find the program, or have the user install it or say where it is.
        while (true)
        {
            var version = await environment.ClaudeCodeVersionAsync(claudeCode.ExecutablePath, ct);
            if (version is not null)
            {
                console.MarkupLine($"[green]Found Claude Code[/] ({Markup.Escape(version)}).");
                break;
            }

            console.MarkupLine("[yellow]Claude Code isn't installed on this PC[/], or isn't where it was looked for. Install it from");
            console.MarkupLine("[link]https://code.claude.com[/], then run [bold]claude[/] once and sign in.");
            var next = ui.Choose("Then:",
                ("retry", "I've installed it: look again"),
                ("path", "It's installed somewhere else: give the path"),
                ("back", "Choose another way to write the notes"));
            if (next == "back") return false;
            if (next == "path")
                claudeCode.ExecutablePath = console.Prompt(
                    new TextPrompt<string>("Path to the claude program:").DefaultValue(claudeCode.ExecutablePath)).Trim().Trim('"');
        }

        claudeCode.Model = ui.Choose("Which Claude model?",
            ("opus", "opus     [dim]the one our tests were made with[/]"),
            ("sonnet", "sonnet   [dim]quicker, and lighter on the plan's limits[/]"),
            ("haiku", "haiku    [dim]quickest[/]"));

        // Say whose account the summaries will count toward.
        var account = await environment.ClaudeCodeAccountAsync(claudeCode.ExecutablePath, ct);
        var text = Markup.Escape(account?.Describe() ?? "Couldn't read the Claude Code login.");
        console.MarkupLine(account?.UsesSubscription == true ? $"[green]{text}[/]" : $"[yellow]{text}[/]");

        ai.Backend = Backend;
        return true;
    }
}
