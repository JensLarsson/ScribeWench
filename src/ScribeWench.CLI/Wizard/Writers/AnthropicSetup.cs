using ScribeWench.Core.Models;
using Spectre.Console;

namespace ScribeWench.CLI.Wizard.Writers;

// Claude, with an Anthropic API key.
public sealed class AnthropicSetup(WizardUi ui, IAnsiConsole console) : IWriterSetup
{
    public string Backend => "anthropic";

    public int Order(WriterFindings found) => 20;

    public string Label(WriterFindings found) => "Claude, with an Anthropic API key   [dim]paid per session[/]";

    public string Describe(AiConfig ai) => $"Claude ({ai.Anthropic.Model}), with an Anthropic API key";

    public Task<bool> ConfigureAsync(AiConfig ai, CancellationToken ct)
    {
        var anthropic = ai.Anthropic;
        console.MarkupLine("[dim]Keys are made at platform.claude.com. Max and Ultra plans include monthly API credit: claude.ai > Settings >[/]");
        console.MarkupLine("[dim]Billing > API credits > Link organization. Leave the key empty to use the ANTHROPIC_API_KEY environment variable.[/]");

        anthropic.ApiKey = ui.AskSecret("Anthropic API key", anthropic.ApiKey, allowEmpty: true);

        var model = ui.Choose("Which Claude model?",
            ("claude-opus-5-5", "claude-opus-5-5     [dim]the one our tests were made with[/]"),
            ("claude-sonnet-5-5", "claude-sonnet-5-5   [dim]quicker and cheaper[/]"),
            ("claude-haiku-5-5", "claude-haiku-5-5    [dim]quickest and cheapest[/]"),
            ("", "Another model: type its name"));
        anthropic.Model = model.Length > 0
            ? model
            : console.Prompt(new TextPrompt<string>("Model name:").DefaultValue(anthropic.Model)).Trim();

        ai.Backend = Backend;
        return Task.FromResult(true);
    }
}
