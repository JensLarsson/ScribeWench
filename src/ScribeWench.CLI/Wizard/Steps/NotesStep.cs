using ScribeWench.CLI.Wizard.Environments;
using ScribeWench.Core;
using ScribeWench.Core.Models;
using Spectre.Console;

namespace ScribeWench.CLI.Wizard.Steps;

// Where the notes go: the notes folder, and whether transcripts are kept and summaries posted.
public sealed class NotesStep(ISetupEnvironment environment, IAnsiConsole console) : IWizardStep
{
    public string Title => "Where the notes go";
    public string MenuLabel => "Change where the notes go";

    public Task RunAsync(AppConfig config, bool fromMenu, CancellationToken ct)
    {
        // Without a notes folder, summaries need somewhere else to go.
        if (!AskForNotesFolder(config.Obsidian))
            config.Output.Directory = console.Prompt(
                new TextPrompt<string>("Folder for summaries:")
                    .DefaultValue(config.Output.Directory));

        config.Output.SaveMarkdown = console.Confirm("Keep the full transcripts as well?", defaultValue: config.Output.SaveMarkdown);
        config.Output.PostToDiscord = console.Confirm("Post each summary in Discord?", defaultValue: config.Output.PostToDiscord);
        return Task.CompletedTask;
    }

    // Returns whether one is set.
    private bool AskForNotesFolder(ObsidianConfig obsidian)
    {
        console.MarkupLine("[dim]Each game gets its own folder of notes in here, as plain Markdown files. If you use Obsidian, give the[/]");
        console.MarkupLine("[dim]path of your vault (or a folder in it) and the notes link up with your own. Type [bold]none[/] for no notes folder.[/]");

        var current = string.IsNullOrWhiteSpace(obsidian.VaultPath) ? ObsidianConfig.DefaultVaultPath : obsidian.VaultPath;
        while (true)
        {
            var path = console.Prompt(
                new TextPrompt<string>("Notes folder:")
                    .DefaultValue(current)).Trim().Trim('"');

            if (path.Equals("none", StringComparison.OrdinalIgnoreCase))
            {
                obsidian.VaultPath = string.Empty;
                return false;
            }

            var full = PathHelper.Expand(path);
            if (!environment.FolderExists(full))
            {
                if (!console.Confirm($"{Markup.Escape(full)} doesn't exist. Create it?", defaultValue: true))
                    continue;
                try
                {
                    environment.CreateFolder(full);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
                {
                    console.MarkupLine($"[red]Couldn't create it:[/] {Markup.Escape(ex.Message)}");
                    continue;
                }
            }

            obsidian.VaultPath = path;
            return true;
        }
    }
}
