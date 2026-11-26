using ScribeWench.CLI.Wizard.Environments;
using ScribeWench.Core.Models;
using Spectre.Console;

namespace ScribeWench.CLI.Wizard.Steps;

// Transcription: which Whisper model, and whether it runs on a graphics card.
public sealed class TranscriptionStep(ISetupEnvironment environment, SetupSession session, WizardUi ui, IAnsiConsole console) : IWizardStep
{
    public string Title => "Transcription";
    public string MenuLabel => "Change transcription";

    public async Task RunAsync(AppConfig config, bool fromMenu, CancellationToken ct)
    {
        var whisper = config.Whisper;

        // The card does the work when there is one; the processor otherwise. A small one could be a
        // real card, which helps Whisper, or graphics built into the processor, which doesn't, and
        // nothing tells the two apart: ask.
        var machine = session.Machine();
        var card = machine.Card;
        var small = card is null ? machine.Cards.Where(c => c.MemoryGb >= 1.5).MaxBy(c => c.MemoryGb) : null;
        if (small is null)
        {
            console.MarkupLine($"This PC has {Markup.Escape(machine.Describe())}.");
        }
        else
        {
            console.MarkupLine($"This PC has {machine.NominalRamGb:0} GB of RAM. Its graphics ({Markup.Escape(small.Name)}, {small.MemoryGb:0.#} GB) may be a small separate");
            console.MarkupLine("card, which speeds up transcription, or built into the processor, which doesn't.");
            if (console.Confirm("Is it a separate graphics card?", defaultValue: !session.FirstRun && whisper.GpuDevice >= 0))
                card = small;
        }
        console.MarkupLine("[dim]Whisper turns the recording into text, on this PC. A larger model hears better and takes longer.[/]");

        // The model, the one that suits the card first.
        var recommended = WhisperCatalog.Recommended(card?.MemoryGb ?? 0);
        var choices = WhisperCatalog.Models
            .OrderByDescending(m => m.Key == recommended)
            .Select(m => (m.Key, $"{m.Key,-7}{WizardUi.Size(m.Bytes),7}   {m.About}{(m.Key == recommended ? "   [green]suits this PC[/]" : "")}{(!session.FirstRun && m.Key == whisper.ModelSize ? "   [dim]current[/]" : "")}"))
            .ToArray();
        whisper.ModelSize = ui.Choose("Which Whisper model?", choices);

        whisper.GpuDevice = card is not null ? 0 : -1;
        console.MarkupLine(card is not null
            ? $"[dim]Transcription will run on the {Markup.Escape(card.Name)}.[/]"
            : "[dim]Transcription will run on the processor.[/]");

        // Download it, unless it is there or the user would rather wait.
        if (environment.WhisperModelExists(whisper))
        {
            console.MarkupLine("[dim]That model is already downloaded.[/]");
            return;
        }
        if (!console.Confirm($"Download it now ({WizardUi.Size(WhisperCatalog.Bytes(whisper.ModelSize))})?", defaultValue: true))
        {
            console.MarkupLine("[dim]It will be downloaded when the first recording starts.[/]");
            return;
        }
        await DownloadAsync(whisper, ct);
    }

    public async Task DownloadAsync(WhisperConfig whisper, CancellationToken ct)
    {
        var bytes = WhisperCatalog.Bytes(whisper.ModelSize);
        try
        {
            await console.Progress()
                .AutoClear(false)
                .Columns(WizardUi.DownloadColumns())
                .StartAsync(async context =>
                {
                    var task = context.AddTask($"Whisper {whisper.ModelSize}", maxValue: bytes);
                    // The size is the one the model had when this was written; hold just short of full until it is done.
                    await environment.DownloadWhisperModelAsync(whisper, received => task.Value = Math.Min(received, bytes * 0.99), ct);
                    task.Value = task.MaxValue;
                });
            console.MarkupLine("[green]Downloaded.[/]");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            console.MarkupLine($"[red]The download failed:[/] {Markup.Escape(ex.Message)}");
            console.MarkupLine("[dim]It will be tried again when the first recording starts.[/]");
        }
    }
}
