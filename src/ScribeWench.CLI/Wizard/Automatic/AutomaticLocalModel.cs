using System.Text.Json;
using ScribeWench.AI.Backends;
using ScribeWench.CLI.Wizard.Environments;
using ScribeWench.CLI.Wizard.Steps;
using ScribeWench.CLI.Wizard.Writers;
using ScribeWench.Core.Models;
using Spectre.Console;

namespace ScribeWench.CLI.Wizard.Automatic;

// A local model: setup installs Ollama if it has to, downloads the best model there is room for,
// and sees that it loads.
public sealed class AutomaticLocalModel(
    LocalModelSetup local,
    SummariesStep summaries,
    OllamaClient ollamaClient,
    IOllamaProgram program,
    ISetupEnvironment environment,
    IAnsiConsole console) : IAutomaticWriter
{
    // Disk an Ollama install takes: its 2 GB download, and 3.3 GB once installed.
    private const double OllamaInstallGb = 6;

    public (int Order, string Label)? Offer(Findings found) => found.Models.Count > 0
        ? (1, $"A local model on this PC   [dim]free and private; setup installs and downloads it ({found.Models[0].Name}, {found.Models[0].DownloadGb:0} GB)[/]")
        : null;

    public string? Notice(Findings found) => found.Models.Count == 0
        ? $"[yellow]It is small for a local model:[/] the smallest that wrote usable notes in our tests wants {LocalModels.All[^1].MemoryGb + LocalModels.HeadroomGb:0} GB of RAM and graphics memory together."
        : null;

    public async Task<AutomaticPlan?> PlanAsync(AppConfig config, Findings found, MachineInfo machine, bool canGoBack, CancellationToken ct)
    {
        var (models, installed, canInstall, downloaded, remark) = await console.Status().StartAsync(
            "Looking at what a local model needs here...", _ => LookAsync(found.Models, ct));
        if (models.Count == 0)
            return AutomaticPlan.Impossible(remark ?? "A local model can't be set up on this PC.");

        var model = models[0];
        var todo = new List<string>();
        if (!installed)
            todo.Add(canInstall ? "install Ollama (2 GB)" : "you install Ollama (setup shows how)");
        todo.Add(downloaded ? "[dim]already downloaded[/]" : $"download {model.DownloadGb:0} GB");

        return new AutomaticPlan(
            $"{model.Name}, a local model run by Ollama [dim]({LocalModels.Pace(model, machine.CardGb)} a session)[/]",
            string.Join(", ", todo),
            async token =>
            {
                if (await SetUpAsync(config.Ai, models, token)) return;
                console.MarkupLine("[yellow]A local model couldn't be set up without you.[/] The choice is yours, then:");
                await summaries.ConfigureAsync(config.Ai, token);
            })
        {
            Remark = remark,
        };
    }

    // Which of the models the disk has room for, best first, and what has to be installed and
    // downloaded for the best of them. Remark: why a better model was passed over, when one was.
    private async Task<(IReadOnlyList<LocalModel> Models, bool Installed, bool CanInstall, bool Downloaded, string? Remark)> LookAsync(
        IReadOnlyList<LocalModel> fitting, CancellationToken ct)
    {
        // Ollama, and what it already has.
        var baseUrl = OllamaClient.DefaultBaseUrl;
        var version = await ollamaClient.GetVersionAsync(baseUrl, ct);
        var installed = version is not null || program.Installed;
        if (version is null && installed)
            version = await ollamaClient.EnsureRunningAsync(baseUrl, ct: ct);

        IReadOnlyList<OllamaModel> downloaded = [];
        if (version is not null)
        {
            try
            {
                downloaded = await ollamaClient.ListAsync(baseUrl, ct);
            }
            catch (Exception ex) when (ex is OllamaException or HttpRequestException or JsonException) { }
        }
        bool Have(LocalModel model) => downloaded.Any(d => OllamaClient.SameModel(d.Name, model.Name));

        // The models the disk has room for.
        var free = environment.FreeDiskGbForOllamaModels();
        double DiskFor(LocalModel model) => (Have(model) ? 0 : model.DownloadGb + 2) + (installed ? 0 : OllamaInstallGb);

        var models = fitting.Where(m => free is null || free >= DiskFor(m)).ToList();
        string? remark = null;
        if (models.Count == 0)
            remark = $"A local model would run on this PC, but its disk is too full: {fitting[^1].Name} needs {DiskFor(fitting[^1]):0} GB free and there are {free:0}.";
        else if (models[0] != fitting[0])
            remark = $"{fitting[0].Name} would write better notes here, but needs {DiskFor(fitting[0]):0} GB of free disk and there are {free:0}.";

        return (models, installed, installed || await local.CanInstallAsync(ct), models.Count > 0 && Have(models[0]), remark);
    }

    // Gets Ollama running and the first of the models that loads on this PC. False when that needs the user after all.
    private async Task<bool> SetUpAsync(AiConfig ai, IReadOnlyList<LocalModel> models, CancellationToken ct)
    {
        var ollama = ai.Ollama;
        ollama.BaseUrl = OllamaClient.DefaultBaseUrl;

        if (!await local.ConnectAsync(ollama, ct, installWithoutAsking: true)) return false;
        // The user pointed at an Ollama on another computer instead, and the models were picked for this one.
        if (!OllamaClient.IsLocal(ollama.BaseUrl)) return await local.ConfigureAsync(ai, ct);

        foreach (var model in models)
        {
            bool have;
            try
            {
                have = await ollamaClient.HasAsync(ollama.BaseUrl, model.Name, ct);
            }
            catch (Exception ex) when (ex is OllamaException or HttpRequestException or JsonException)
            {
                have = false;
            }

            if (!have)
            {
                console.MarkupLine($"Downloading {Markup.Escape(model.Name)} ({model.DownloadGb:0} GB).");
                if (await local.DownloadAsync(ollama.BaseUrl, model.Name, onThisPc: true, ct, ask: false) != Download.Done)
                    return false;
            }

            var (contextSize, _) = await local.ContextSizeForAsync(ollama.BaseUrl, model.Name, ct);
            if (await local.TryAsync(ollama.BaseUrl, model.Name, contextSize, ct))
            {
                ollama.Model = model.Name;
                ollama.ContextSize = contextSize;
                ollama.Think = false;
                ai.Backend = local.Backend;
                return true;
            }

            if (model != models[^1])
                console.MarkupLine("[dim]Trying the next size down.[/]");
        }
        return false;
    }
}
