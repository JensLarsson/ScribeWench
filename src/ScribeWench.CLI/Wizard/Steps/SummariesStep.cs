using ScribeWench.AI.Backends;
using ScribeWench.CLI.Wizard.Environments;
using ScribeWench.CLI.Wizard.Writers;
using ScribeWench.Core.Models;
using Spectre.Console;

namespace ScribeWench.CLI.Wizard.Steps;

// Who writes the session notes: the ways there are, the one that suits this PC first. Each of them
// can be backed out of, which leads back to the list.
public sealed class SummariesStep(
    IEnumerable<IWriterSetup> writers,
    ISetupEnvironment environment,
    SetupSession session,
    WizardUi ui,
    IAnsiConsole console) : IWizardStep
{
    public string Title => "Who writes the session notes";
    public string MenuLabel => "Change who writes the session notes";

    public Task RunAsync(AppConfig config, bool fromMenu, CancellationToken ct) => ConfigureAsync(config.Ai, ct);

    public async Task ConfigureAsync(AiConfig ai, CancellationToken ct)
    {
        var machine = session.Machine();
        var found = new WriterFindings(
            await environment.ClaudeCodeVersionAsync(ai.ClaudeCode.ExecutablePath, ct) is not null,
            LocalModels.Recommend(machine.RamGb, machine.CardGb));

        console.MarkupLine("[dim]This decides how good the notes are. In our tests Claude wrote clearly the best ones.[/]");
        console.MarkupLine("[dim]A local model is free and keeps everything on this PC, and a good one needs a strong PC.[/]");

        var choices = writers.OrderBy(w => w.Order(found)).Select(w => (w, w.Label(found))).ToArray();
        while (true)
        {
            if (await ui.Choose("Who writes the session notes?", choices).ConfigureAsync(ai, ct))
                return;
        }
    }

    // Who writes the notes, in a line: the backend and the model it uses.
    public string Describe(AiConfig ai) =>
        writers.FirstOrDefault(w => w.Backend == ai.Backend.ToLowerInvariant())?.Describe(ai) ?? ai.Backend;
}
