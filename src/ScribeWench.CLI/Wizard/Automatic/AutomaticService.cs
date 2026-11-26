using ScribeWench.CLI.Wizard.Environments;
using ScribeWench.CLI.Wizard.Steps;
using ScribeWench.CLI.Wizard.Writers;
using ScribeWench.Core.Models;
using Spectre.Console;

namespace ScribeWench.CLI.Wizard.Automatic;

// A service the user has an account with. Always on the list; it has questions of its own, asked
// when it is chosen.
public sealed class AutomaticService(AnthropicSetup anthropic, ServiceSetup service, SummariesStep summaries, WizardUi ui) : IAutomaticWriter
{
    public (int Order, string Label)? Offer(Findings found) =>
        (3, "A service you have an account with   [dim]Claude by API key, OpenAI, DeepSeek, OpenRouter and others[/]");

    public string? Notice(Findings found) => null;

    public async Task<AutomaticPlan?> PlanAsync(AppConfig config, Findings found, MachineInfo machine, bool canGoBack, CancellationToken ct)
    {
        while (true)
        {
            var choices = new List<(string, string)>
            {
                ("anthropic", "Claude, with an Anthropic API key   [dim]the best notes in our tests[/]"),
                ("other", "OpenAI, DeepSeek, Kimi, Gemini, OpenRouter or another service   [dim]with its API key[/]"),
            };
            if (canGoBack) choices.Add(("back", "Back"));

            var configured = ui.Choose("Which service?", choices.ToArray()) switch
            {
                "back" => (bool?)null,
                "anthropic" => await anthropic.ConfigureAsync(config.Ai, ct),
                _ => await service.ConfigureAsync(config.Ai, ct),
            };
            if (configured is null) return null;
            if (configured == true)
                return new AutomaticPlan(Markup.Escape(summaries.Describe(config.Ai)), "[dim]nothing to download[/]", _ => Task.CompletedTask);
        }
    }
}
