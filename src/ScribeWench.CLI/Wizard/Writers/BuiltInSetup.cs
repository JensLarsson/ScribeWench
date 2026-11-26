using ScribeWench.CLI.Wizard.Environments;
using ScribeWench.Core;
using ScribeWench.Core.Models;
using Spectre.Console;

namespace ScribeWench.CLI.Wizard.Writers;

// A GGUF file with the built-in runner (LLamaSharp).
public sealed class BuiltInSetup(ISetupEnvironment environment, ScribePaths paths, WizardUi ui, IAnsiConsole console) : IWriterSetup
{
    private sealed record Preset(string Name, string Url, float FileSizeGb, string ChatTemplate);

    private static readonly Preset[] Presets =
    [
        new("Mistral Nemo 12B", "https://huggingface.co/bartowski/Mistral-Nemo-Instruct-2407-GGUF/resolve/main/Mistral-Nemo-Instruct-2407-Q4_K_M.gguf", 7.2f, "mistral"),
        new("Llama 3.1 8B", "https://huggingface.co/bartowski/Meta-Llama-3.1-8B-Instruct-GGUF/resolve/main/Meta-Llama-3.1-8B-Instruct-Q6_K.gguf", 6.6f, "llama3"),
        new("Gemma 2 9B", "https://huggingface.co/bartowski/gemma-2-9b-it-GGUF/resolve/main/gemma-2-9b-it-Q4_K_M.gguf", 5.8f, "gemma"),
        new("Mistral 7B", "https://huggingface.co/TheBloke/Mistral-7B-Instruct-v0.2-GGUF/resolve/main/mistral-7b-instruct-v0.2.Q4_K_M.gguf", 4.4f, "mistral"),
        new("Llama 3.2 3B", "https://huggingface.co/bartowski/Llama-3.2-3B-Instruct-GGUF/resolve/main/Llama-3.2-3B-Instruct-Q4_K_M.gguf", 1.9f, "llama3"),
    ];

    public string Backend => "llamasharp";

    public int Order(WriterFindings found) => 40;

    public string Label(WriterFindings found) => "A GGUF model file with the built-in runner   [dim]the old way: short sessions only[/]";

    public string Describe(AiConfig ai) => $"{Path.GetFileName(ai.LlamaSharp.ModelPath)}, with the built-in runner";

    public async Task<bool> ConfigureAsync(AiConfig ai, CancellationToken ct)
    {
        var llama = ai.LlamaSharp;
        console.MarkupLine("[yellow]This is the old way, kept for those who use it.[/] It runs on the processor only, with models from 2024 and a small");
        console.MarkupLine("context window: it can't use the game's notes, can't do `edit`, and loses the thread of a long session.");
        console.MarkupLine("[dim]For a local model, \"A local model on this PC\" is the better choice.[/]");

        // Which model: one of the old presets, or a file of the user's own.
        var choices = Presets
            .Select(p => ((Preset?)p, $"{p.Name,-18}{p.FileSizeGb,4:0.#} GB"))
            .Append((new Preset("", "", 0, ""), "A GGUF file of your own: give its path or download address"))
            .Append((null, "Back"))
            .ToArray();
        var preset = ui.Choose("Which model?", choices);
        if (preset is null) return false;

        if (preset.Url.Length > 0)
        {
            llama.DownloadUrl = preset.Url;
            llama.ModelPath = Path.Combine(paths.LlmModels, Path.GetFileName(new Uri(preset.Url).LocalPath));
            llama.ChatTemplate = preset.ChatTemplate;
        }
        else
        {
            var source = console.Prompt(new TextPrompt<string>("Path of the file, or the address to download it from:").DefaultValue(llama.ModelPath)).Trim().Trim('"');
            if (WebAddress.IsOne(source))
            {
                llama.DownloadUrl = source;
                llama.ModelPath = Path.Combine(paths.LlmModels, Path.GetFileName(new Uri(source).LocalPath));
            }
            else
            {
                llama.ModelPath = source;
            }
            llama.ChatTemplate = ui.Choose("Which prompt format does the model use? Its page says.",
                ("mistral", "Mistral ([[INST]] ... [[/INST]])"),
                ("llama3", "Llama 3"),
                ("gemma", "Gemma"));
        }

        llama.ContextSize = console.Prompt(
            new TextPrompt<int>("Context window in tokens:")
                .DefaultValue(llama.ContextSize)
                .Validate(v => v >= 1024 ? ValidationResult.Success() : ValidationResult.Error("At least 1024")));

        // Download it now, or leave that for the first session.
        var path = PathHelper.Expand(llama.ModelPath);
        if (!environment.FileExists(path) && !string.IsNullOrEmpty(llama.DownloadUrl))
        {
            llama.AutoDownload = true;
            if (console.Confirm("Download the model now?", defaultValue: true))
                await DownloadAsync(llama.DownloadUrl, path, ct);
            else
                console.MarkupLine("[dim]It will be downloaded when the first session ends.[/]");
        }
        else if (!environment.FileExists(path))
        {
            console.MarkupLine($"[yellow]There is no file at {Markup.Escape(path)} yet.[/] Put the model there before the first session.");
        }

        ai.Backend = Backend;
        return true;
    }

    private async Task DownloadAsync(string url, string path, CancellationToken ct)
    {
        try
        {
            await console.Progress()
                .AutoClear(false)
                .Columns(WizardUi.DownloadColumns())
                .StartAsync(async context =>
                {
                    var task = context.AddTask(Markup.Escape(Path.GetFileName(path)), maxValue: 1);
                    task.IsIndeterminate = true;
                    await environment.DownloadFileAsync(url, path, (received, total) =>
                    {
                        if (total <= 0) return;
                        task.IsIndeterminate = false;
                        task.MaxValue = total;
                        task.Value = received;
                    }, ct);
                    task.IsIndeterminate = false;
                    task.Value = task.MaxValue;
                });
            console.MarkupLine($"[green]Downloaded to[/] {Markup.Escape(path)}");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException)
        {
            console.MarkupLine($"[red]The download failed:[/] {Markup.Escape(ex.Message)}");
            console.MarkupLine("[dim]It will be tried again when the first session ends.[/]");
        }
    }
}
