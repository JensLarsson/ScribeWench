using System.Text.Json;
using ScribeWench.AI.Backends;
using ScribeWench.AI.Summaries;
using ScribeWench.CLI.Wizard.Environments;
using ScribeWench.Core.Models;
using Spectre.Console;

namespace ScribeWench.CLI.Wizard.Writers;

public enum Download { Done, Skipped, Failed }

// A local model, run by Ollama on this PC or on another computer.
public sealed class LocalModelSetup(
    OllamaClient ollamaClient,
    IOllamaProgram program,
    ISetupEnvironment environment,
    SetupSession session,
    WizardUi ui,
    IAnsiConsole console) : IWriterSetup
{
    private bool? _canInstall;

    public string Backend => "ollama";

    // Second where Claude Code leads the list or this PC can run a model; after the services where it can't.
    public int Order(WriterFindings found) => found.ClaudeCodeInstalled || found.LocalModel is not null ? 10 : 25;

    public string Label(WriterFindings found) => found.LocalModel is not null
        ? $"A local model on this PC   [dim]free and private; this PC can run {Markup.Escape(found.LocalModel.Name)}[/]"
        : "A local model on this PC   [dim]free and private; this PC is small for one[/]";

    public string Describe(AiConfig ai) =>
        $"{ai.Ollama.Model}, a local model run by Ollama" + (OllamaClient.IsLocal(ai.Ollama.BaseUrl) ? "" : $" at {ai.Ollama.BaseUrl}");

    public async Task<bool> ConfigureAsync(AiConfig ai, CancellationToken ct)
    {
        var ollama = ai.Ollama;
        var machine = session.Machine();
        console.MarkupLine("[dim]Local models are run by Ollama, a free program. Nothing leaves this PC.[/]");

        if (!await ConnectAsync(ollama, ct)) return false;
        var onThisPc = OllamaClient.IsLocal(ollama.BaseUrl);

        while (true)
        {
            // Which model: one of ours, one Ollama already has, or any other by name.
            IReadOnlyList<OllamaModel> downloaded;
            try
            {
                downloaded = await ollamaClient.ListAsync(ollama.BaseUrl, ct);
            }
            catch (Exception ex) when (ex is OllamaException or HttpRequestException or JsonException)
            {
                downloaded = [];
            }

            var name = ChooseModel(machine, downloaded, onThisPc);
            if (name is null) return false;
            if (name.Length == 0)
            {
                console.MarkupLine("[dim]Any model on [link]https://ollama.com/library[/] works, by the name shown there (name:size, like the ones above).[/]");
                console.MarkupLine("[dim]A GGUF file on Hugging Face works too, as hf.co/user/repository:quantization.[/]");
                name = console.Prompt(
                    new TextPrompt<string>("Model name:")
                        .Validate(text => text.Trim().Length == 0 || text.Trim().Contains(' ')
                            ? ValidationResult.Error("A model name has no spaces, for example qwen3.8:27b")
                            : ValidationResult.Success()))
                    .Trim();
            }

            // Download it, unless Ollama has it.
            var have = downloaded.Any(m => OllamaClient.SameModel(m.Name, name));
            if (!have)
            {
                var download = await DownloadAsync(ollama.BaseUrl, name, onThisPc, ct);
                if (download == Download.Failed) continue;
                have = download == Download.Done;
            }

            // The context window, and for a model that isn't one of ours, the settings we haven't tested.
            var think = false;
            var (contextSize, details) = have ? await ContextSizeForAsync(ollama.BaseUrl, name, ct) : (LocalModels.ContextSize, null);

            if (LocalModels.Find(name) is null && console.Confirm("Change the advanced settings (context window, thinking)?", defaultValue: false))
            {
                var most = details is { ContextLength: > 0 } ? details.ContextLength : int.MaxValue;
                console.MarkupLine("[dim]The context window is how much the model reads at once. 65,536 tokens holds a three-hour session; a larger[/]");
                console.MarkupLine("[dim]window takes more memory. Whatever doesn't fit is read in parts.[/]");
                contextSize = console.Prompt(
                    new TextPrompt<int>("Context window in tokens:")
                        .DefaultValue(contextSize)
                        .Validate(v => v < ChunkedSummarizer.MinContextTokens ? ValidationResult.Error($"At least {ChunkedSummarizer.MinContextTokens:N0}")
                            : v > most ? ValidationResult.Error($"This model reads at most {most:N0}")
                            : ValidationResult.Success()));
                if (details?.CanThink == true)
                    think = console.Confirm("Let the model think before it answers? Slower, and the thinking takes room in the window.", defaultValue: false);
            }

            // See that it loads here, so a model too large shows up now and not after the first session.
            if (have && !await TryAsync(ollama.BaseUrl, name, contextSize, ct)
                && ui.Choose("Then:", (true, "Pick another model"), (false, "Keep this one anyway")))
                continue;

            ollama.Model = name;
            ollama.ContextSize = contextSize;
            ollama.Think = think;
            ai.Backend = Backend;
            return true;
        }
    }

    // The usual context window, or the model's own limit when that is lower.
    public async Task<(int ContextSize, OllamaModel? Details)> ContextSizeForAsync(string baseUrl, string name, CancellationToken ct)
    {
        OllamaModel? details = null;
        try
        {
            details = await ollamaClient.ShowAsync(baseUrl, name, ct);
        }
        catch (Exception ex) when (ex is OllamaException or HttpRequestException or JsonException) { }

        if (details is not { ContextLength: > 0 } || details.ContextLength >= LocalModels.ContextSize)
            return (LocalModels.ContextSize, details);

        console.MarkupLine($"[dim]{Markup.Escape(name)} reads at most {details.ContextLength:N0} tokens at once, so a long session will be read in parts.[/]");
        return (Math.Max(ChunkedSummarizer.MinContextTokens, details.ContextLength), details);
    }

    public async Task<bool> CanInstallAsync(CancellationToken ct) =>
        _canInstall ??= await environment.CanInstallOllamaAsync(ct);

    private async Task<bool> InstallAsync(CancellationToken ct)
    {
        console.MarkupLine("Installing Ollama. Windows' package manager (winget) downloads it from Ollama's own releases, about 2 GB,");
        console.MarkupLine("checks the download and installs it for this user. [dim]What it is doing shows below.[/]");

        var problem = await environment.InstallOllamaAsync(ct);
        if (problem is null)
        {
            console.MarkupLine("[green]Ollama is installed.[/]");
            return true;
        }
        console.MarkupLine($"[yellow]Installing it didn't work:[/] {Markup.Escape(problem)}");
        return false;
    }

    // Finds a running Ollama: at the configured address, by starting the installed one, by
    // installing it, by waiting for the user to install it, or on another computer. False when the
    // user backs out. installWithoutAsking: the user has already agreed to Ollama being installed.
    public async Task<bool> ConnectAsync(OllamaConfig ollama, CancellationToken ct, bool installWithoutAsking = false)
    {
        var announced = false;
        while (true)
        {
            // Is it there? Start it if it only needs starting.
            var onThisPc = OllamaClient.IsLocal(ollama.BaseUrl);
            var installed = onThisPc && program.Installed;

            var version = await ollamaClient.GetVersionAsync(ollama.BaseUrl, ct);
            if (version is null && installed)
                version = await console.Status().StartAsync("Starting Ollama...",
                    _ => ollamaClient.EnsureRunningAsync(ollama.BaseUrl, ct: ct));

            if (version is not null)
            {
                console.MarkupLine($"[dim]Ollama {Markup.Escape(version)} is running{(onThisPc ? "" : $" at {Markup.Escape(ollama.BaseUrl)}")}.[/]");
                return true;
            }

            // Install it, where that was agreed to already. Once: if it fails, the choices below take over.
            var canInstall = onThisPc && !installed && await CanInstallAsync(ct);
            if (canInstall && installWithoutAsking)
            {
                installWithoutAsking = false;
                if (await InstallAsync(ct)) continue;
            }

            // Say what is missing.
            if (!onThisPc)
                console.MarkupLine($"[yellow]Nothing answers at {Markup.Escape(ollama.BaseUrl)}.[/] Ollama has to be running there, and set to accept connections from other computers.");
            else if (installed)
                console.MarkupLine("[yellow]Ollama is installed but wouldn't start.[/] Start it from the Start menu.");
            else if (!announced)
            {
                console.MarkupLine(canInstall
                    ? $"[yellow]Ollama isn't installed on this PC.[/] Setup can install it, or you can get it from [link]{OllamaClient.DownloadPage}[/] yourself:"
                    : $"[yellow]Ollama isn't installed on this PC.[/] Get it from [link]{OllamaClient.DownloadPage}[/] and run the installer:");
                console.MarkupLine("[dim]about 2 GB, free, no account needed. It starts by itself when the installer is done.[/]");
            }
            else
                console.MarkupLine("[yellow]Still no Ollama on this PC.[/] It answers a few seconds after the installer finishes.");
            announced = true;

            // And what can be done about it.
            var choices = new List<(string, string)>();
            if (canInstall) choices.Add(("install", "Install it for me"));
            if (onThisPc && !installed) choices.Add(("open", "Open the download page in my browser"));
            choices.Add(("retry", onThisPc && !installed ? "I've installed it: look again" : "Look again"));
            choices.Add(("remote", onThisPc ? "Ollama runs on another computer: give its address" : "Give another address"));
            if (!onThisPc) choices.Add(("here", "Use Ollama on this PC instead"));
            choices.Add(("back", "Choose another way to write the notes"));

            switch (ui.Choose("Then:", choices.ToArray()))
            {
                case "back":
                    return false;
                case "install":
                    await InstallAsync(ct);
                    break;
                case "open":
                    if (!environment.OpenInBrowser(OllamaClient.DownloadPage))
                        console.MarkupLine($"[dim]Couldn't open a browser. Go to {OllamaClient.DownloadPage} yourself.[/]");
                    console.Confirm("Installed?", defaultValue: true);
                    break;
                case "here":
                    ollama.BaseUrl = OllamaClient.DefaultBaseUrl;
                    announced = false;
                    break;
                case "remote":
                    ollama.BaseUrl = console.Prompt(
                        new TextPrompt<string>("Address of that Ollama:")
                            .DefaultValue(onThisPc ? "http://192.168.1.10:11434" : ollama.BaseUrl)
                            .Validate(text => WebAddress.IsOne(text) ? ValidationResult.Success() : ValidationResult.Error("An address like http://192.168.1.10:11434")))
                        .Trim().TrimEnd('/');
                    break;
            }
        }
    }

    // Our models with what testing showed, then whatever else Ollama already has. Null = back, empty = another one.
    private string? ChooseModel(MachineInfo machine, IReadOnlyList<OllamaModel> downloaded, bool onThisPc)
    {
        // The machine's size says nothing about an Ollama on another computer.
        var recommended = onThisPc ? LocalModels.Recommend(machine.RamGb, machine.CardGb) : null;
        var smallest = LocalModels.All[^1];
        if (onThisPc && recommended is null)
        {
            console.MarkupLine($"[yellow]This PC is small for a local model.[/] The smallest that wrote usable notes in our tests wants about {smallest.MemoryGb + LocalModels.HeadroomGb:0} GB of RAM");
            console.MarkupLine($"and graphics memory together, and this PC has {machine.NominalRamGb + machine.CardGb:0}. A service will write better notes here.");
        }
        else
        {
            console.MarkupLine("[dim]A bigger model gets more right; none writes notes as good as Claude's. One too large for the graphics card[/]");
            console.MarkupLine("[dim]runs partly from RAM, which is slower and fine for notes nobody is waiting on.[/]");
            if (onThisPc) console.MarkupLine("[dim]Times are for a three-hour session on this PC. Those over 8 minutes are estimates.[/]");
        }

        bool Have(string name) => downloaded.Any(m => OllamaClient.SameModel(m.Name, name));

        var choices = new List<(string?, string)>();
        foreach (var model in LocalModels.All.OrderByDescending(m => m == recommended))
        {
            var fits = !onThisPc || LocalModels.Fits(model, machine.RamGb, machine.CardGb);
            var notes = new List<string>();
            if (model == recommended) notes.Add("[green]suits this PC[/]");
            if (!fits) notes.Add($"[yellow]wants {model.MemoryGb + LocalModels.HeadroomGb:0} GB of memory[/]");
            if (Have(model.Name)) notes.Add("downloaded");

            var pace = onThisPc && fits ? LocalModels.Pace(model, machine.CardGb) : "";
            choices.Add((model.Name, $"{model.Name,-17}{model.DownloadGb,3:0} GB   {model.Verdict,-31}{pace,-9}{string.Join(", ", notes)}".TrimEnd()));
        }

        foreach (var model in downloaded.Where(m => LocalModels.Find(m.Name) is null))
            choices.Add((model.Name, $"{Markup.Escape(model.Name),-17}{model.SizeBytes / 1e9,3:0} GB   [dim]already downloaded[/]"));

        choices.Add(("", "Another model: type its name"));
        choices.Add((null, "Back"));
        return ui.Choose("Which model?", choices.ToArray());
    }

    // ask: false when the user has already agreed to the download.
    public async Task<Download> DownloadAsync(string baseUrl, string name, bool onThisPc, CancellationToken ct, bool ask = true)
    {
        var known = LocalModels.Find(name);
        if (onThisPc && known is not null && environment.FreeDiskGbForOllamaModels() is { } free && free < known.DownloadGb + 2)
        {
            console.MarkupLine($"[yellow]Not enough disk space:[/] {Markup.Escape(name)} is {known.DownloadGb:0} GB, and the drive Ollama keeps its models on has {free:0} GB free.");
            return Download.Failed;
        }

        if (ask && !console.Confirm($"Download {Markup.Escape(name)} now{(known is not null ? $" ({known.DownloadGb:0} GB)" : "")}?", defaultValue: true))
        {
            console.MarkupLine("[dim]It will be downloaded when the first session ends, which makes that session's notes late.[/]");
            return Download.Skipped;
        }

        try
        {
            await console.Progress()
                .AutoClear(false)
                .Columns(WizardUi.DownloadColumns())
                .StartAsync(async context =>
                {
                    var task = context.AddTask(Markup.Escape(name), maxValue: 1);
                    task.IsIndeterminate = true;
                    await ollamaClient.PullAsync(baseUrl, name, progress =>
                    {
                        if (progress.TotalBytes <= 0) return;
                        task.IsIndeterminate = false;
                        task.MaxValue = progress.TotalBytes;
                        // The last few small files trickle in after the model itself; don't let the bar hover at 99% over them.
                        task.Value = progress.CompletedBytes >= progress.TotalBytes * 0.995 ? progress.TotalBytes : progress.CompletedBytes;
                    }, ct);
                    task.IsIndeterminate = false;
                    task.Value = task.MaxValue;
                });
            console.MarkupLine("[green]Downloaded.[/]");
            return Download.Done;
        }
        catch (Exception ex) when (ex is OllamaException or HttpRequestException or IOException)
        {
            console.MarkupLine($"[red]The download didn't finish:[/] {Markup.Escape(ex.Message)}");
            return Download.Failed;
        }
    }

    // Loads the model once.
    public async Task<bool> TryAsync(string baseUrl, string name, int contextSize, CancellationToken ct)
    {
        var result = await console.Status().StartAsync(
            $"Loading {Markup.Escape(name)} to see that it runs here (a minute or two the first time)...",
            _ => ollamaClient.TryLoadAsync(baseUrl, name, contextSize, TimeSpan.Zero, ct));

        if (!result.Ok)
        {
            console.MarkupLine($"[red]It didn't load:[/] {Markup.Escape(result.Error ?? "Ollama gave no reason")}");
            return false;
        }

        var where = result.GraphicsPercent switch
        {
            null => "",
            >= 99 => ", all of it on the graphics card",
            <= 0 => ", on the processor alone",
            var percent => $", {percent}% of it on the graphics card",
        };
        console.MarkupLine($"[green]It runs.[/] Loaded in {Math.Max(1, result.Elapsed.TotalSeconds):0} s{where}.");
        return true;
    }
}
