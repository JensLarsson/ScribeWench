using ScribeWench.AI.Backends;
using ScribeWench.CLI.Wizard.Environments;
using ScribeWench.CLI.Wizard.Steps;
using ScribeWench.Core;
using ScribeWench.Core.Models;
using Spectre.Console;

namespace ScribeWench.CLI.Wizard.Automatic;

// "Do it for me": everything after the Discord bot. Setup looks at the PC, asks the one thing it
// shouldn't decide for anyone (who writes the notes: something on this PC, or a service with an
// account), shows what it is going to do, and then does it without further questions.
public sealed class AutomaticStep(
    IEnumerable<IAutomaticWriter> writers,
    AutomaticService service,
    TranscriptionStep transcription,
    ISetupEnvironment environment,
    SetupSession session,
    WizardUi ui,
    IAnsiConsole console)
{
    public const string Title = "Everything else";
    public const string MenuLabel = "Do it for me   [dim]let setup pick transcription and who writes the notes for this PC[/]";

    // False when the user turned the plan down. Nothing is changed then, and the steps one at a
    // time ask about it all again.
    public async Task<bool> RunAsync(AppConfig config, CancellationToken ct)
    {
        var machine = session.Machine();
        var found = await console.Status().StartAsync("Looking at this PC...", _ => LookAsync(config, machine, ct));
        console.MarkupLine($"This PC has {Markup.Escape(machine.Describe())}.");

        // The one question: who writes the notes. A way that turns out not to be possible here is
        // said to be, and taken off the list.
        var offered = writers.Where(w => w.Offer(found) is not null).OrderBy(w => w.Offer(found)!.Value.Order).ToList();
        AutomaticPlan plan;
        string? passedOver = null;
        while (true)
        {
            var writer = Ask(found, offered);
            var planned = await writer.PlanAsync(config, found, machine, canGoBack: offered.Count > 1, ct);
            if (planned is null) continue;
            if (!planned.Possible)
            {
                console.MarkupLine($"[yellow]{Markup.Escape(planned.Remark ?? "")}[/]");
                passedOver = planned.Remark;
                offered.Remove(writer);
                continue;
            }
            plan = planned;
            break;
        }

        // The plan, shown before anything is done.
        console.WriteLine();
        console.MarkupLine("Here is what setup will do:");
        var rows = new Grid()
            .AddColumn(new GridColumn().NoWrap().PadLeft(2).PadRight(3))
            .AddColumn(new GridColumn().PadRight(3))
            .AddColumn(new GridColumn());
        rows.AddRow(
            "[bold]Transcription[/]",
            $"Whisper {found.WhisperModel}, on the {(found.WhisperCard is not null ? "graphics card" : "processor")}",
            found.WhisperDownloaded ? "[dim]already downloaded[/]" : $"download {WizardUi.Size(WhisperCatalog.Bytes(found.WhisperModel))}");
        rows.AddRow("[bold]Session notes[/]", plan.What, plan.Todo);
        if (session.FirstRun)
            rows.AddRow("[bold]Notes[/]", $"kept in {Markup.Escape(config.Obsidian.VaultPath)}, and posted in Discord", "");
        console.Write(rows);
        // Why something better isn't in the plan, said again here where the plan is read.
        if ((plan.Remark ?? passedOver) is { } remark)
            console.MarkupLine($"[dim]{Markup.Escape(remark)}[/]");

        console.WriteLine();
        if (!console.Confirm("Go ahead?", defaultValue: true))
            return false;

        // Transcription.
        config.Whisper.ModelSize = found.WhisperModel;
        config.Whisper.GpuDevice = found.WhisperCard is not null ? 0 : -1;
        if (!found.WhisperDownloaded)
            await transcription.DownloadAsync(config.Whisper, ct);

        // Who writes the notes.
        await plan.ApplyAsync(ct);

        // The notes folder keeps its usual place; it only has to exist.
        if (session.FirstRun && !string.IsNullOrWhiteSpace(config.Obsidian.VaultPath))
        {
            var folder = PathHelper.Expand(config.Obsidian.VaultPath);
            try
            {
                if (!environment.FolderExists(folder)) environment.CreateFolder(folder);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                console.MarkupLine($"[yellow]Couldn't create the notes folder {Markup.Escape(folder)}:[/] {Markup.Escape(ex.Message)}");
            }
        }
        return true;
    }

    private async Task<Findings> LookAsync(AppConfig config, MachineInfo machine, CancellationToken ct)
    {
        // Transcription: on the card when there is one worth the name. A small one is left out
        // rather than asked about, since graphics built into a processor would only slow it down.
        var card = machine.Card;
        var whisperModel = WhisperCatalog.Recommended(card?.MemoryGb ?? 0);
        var whisperDownloaded = environment.WhisperModelExists(
            new WhisperConfig { ModelSize = whisperModel, ModelDirectory = config.Whisper.ModelDirectory });

        // Claude Code, and whether it is signed in.
        ClaudeAccount? account = null;
        var claudeInstalled = await environment.ClaudeCodeVersionAsync(config.Ai.ClaudeCode.ExecutablePath, ct) is not null;
        if (claudeInstalled)
            account = await environment.ClaudeCodeAccountAsync(config.Ai.ClaudeCode.ExecutablePath, ct);
        var signedIn = account?.LoggedIn == true;

        var models = LocalModels.All.Where(m => m.Tested && LocalModels.Fits(m, machine.RamGb, machine.CardGb)).ToList();
        return new Findings(whisperModel, card, whisperDownloaded, signedIn ? account : null, claudeInstalled && !signedIn, models);
    }

    // Something on this PC, or a service the user has an account with: the part of setup that is
    // theirs to decide. With only one way open, it isn't asked.
    private IAutomaticWriter Ask(Findings found, List<IAutomaticWriter> offered)
    {
        foreach (var writer in writers)
            if (writer.Notice(found) is { } notice)
                console.MarkupLine(notice);

        // A service is always on the list, so it is what is left.
        if (offered.Count == 1)
        {
            console.MarkupLine("That leaves a service you have an account with to write the session notes.");
            return service;
        }

        console.MarkupLine("[dim]One thing is yours to decide, and it decides how good the notes are. In our tests Claude wrote clearly the best ones.[/]");
        return ui.Choose("Who should write the session notes?", offered.Select(w => (w, w.Offer(found)!.Value.Label)).ToArray());
    }
}
