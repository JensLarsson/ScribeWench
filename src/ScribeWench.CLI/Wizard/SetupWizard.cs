using ScribeWench.CLI.Wizard.Automatic;
using ScribeWench.CLI.Wizard.Environments;
using ScribeWench.CLI.Wizard.Steps;
using ScribeWench.Core.Interfaces;
using ScribeWench.Core.Models;
using Spectre.Console;

namespace ScribeWench.CLI.Wizard;

// Interactive setup. A first run asks for the Discord bot and then either does the rest by itself
// or walks through it, each question with a default that suits the machine. After that, and on
// every later run, a menu changes one part at a time.
public sealed class SetupWizard(
    IEnumerable<IWizardStep> steps,
    DiscordStep discord,
    SummariesStep summaries,
    AutomaticStep automatic,
    IConfigStore configStore,
    ISetupEnvironment environment,
    SetupSession session,
    Banner banner,
    WizardUi ui,
    IAnsiConsole console)
{
    private enum End { Save, Quit }

    public async Task<int> RunAsync(CancellationToken ct = default)
    {
        console.Clear();
        var existing = await configStore.LoadAsync();
        var config = existing ?? new AppConfig();
        session.FirstRun = existing is null;
        var all = steps.ToList();

        // What a first run asks first.
        const string howToSetUp = "How do you want to set up?";
        (bool, string)[] ways =
        [
            (true, "Do it for me   [dim]setup picks what suits this PC, then installs and downloads what that needs[/]"),
            (false, "Let me choose   [dim]four steps, a few questions each, every one with an answer filled in[/]"),
        ];

        // The banner. On a first run it is told what has to fit under it: what is said first, and
        // that question.
        banner.Show(session.FirstRun, ui.RowsOf(Opening) + ui.RowsOf(howToSetUp, ways));
        Opening(console);

        // What the screen says between the banner and its first question.
        void Opening(IAnsiConsole to)
        {
            if (environment.DryRun)
            {
                to.Write(new Rule("[yellow]Dry run[/]").LeftJustified().RuleStyle("yellow"));
                to.MarkupLine("Nothing is downloaded, installed, started or saved. Downloads and installs are acted out in a few seconds,");
                to.MarkupLine("so every screen can be seen. What setup finds on this PC, and what Discord and other services answer, is real.");
                if (environment.Pretence() is { } pretence)
                    to.MarkupLine($"[yellow]Pretending:[/] {Markup.Escape(pretence)}.");
                to.WriteLine();
            }

            if (session.FirstRun)
                to.MarkupLine("Setup needs one thing from you: a Discord bot, which only you can make. The rest it can do by itself.");
        }

        if (existing is null)
        {
            // A first run: the bot, which only the user can make, then the rest by itself or step by step.
            var doItForMe = ui.Choose(howToSetUp, ways);

            ui.Step(1, doItForMe ? 2 : all.Count, discord.Title);
            await discord.RunAsync(config, fromMenu: false, ct);

            if (doItForMe)
            {
                ui.Step(2, 2, AutomaticStep.Title);
                doItForMe = await automatic.RunAsync(config, ct);
                if (!doItForMe) console.MarkupLine("One step at a time, then.");
            }

            if (!doItForMe)
            {
                var number = 1;
                foreach (var step in all.Where(s => s != discord))
                {
                    ui.Step(++number, all.Count, step.Title);
                    await step.RunAsync(config, fromMenu: false, ct);
                }
            }
        }
        else
        {
            console.MarkupLine($"[dim]Settings file:[/] {Markup.Escape(configStore.ConfigPath)}");
            await discord.LookUpSavedBotAsync(config.Discord, ct);
        }

        // The review and the menu, until the settings are saved or given up on.
        var menu = new List<(object, string)> { (End.Save, "Save and finish") };
        menu.AddRange(all.Select(step => ((object)step, step.MenuLabel)));
        menu.Add((automatic, AutomaticStep.MenuLabel));
        menu.Add((End.Quit, "Quit without saving"));

        while (true)
        {
            console.WriteLine();
            ShowReview(config);

            switch (ui.Choose("What next?", menu.ToArray()))
            {
                case IWizardStep step:
                    await step.RunAsync(config, fromMenu: true, ct);
                    break;
                case AutomaticStep:
                    await automatic.RunAsync(config, ct);
                    break;
                case End.Quit:
                    console.MarkupLine("[yellow]Nothing was saved.[/]");
                    return 1;
                case End.Save:
                    return await SaveAsync(config);
            }
        }
    }

    private void ShowReview(AppConfig config)
    {
        var settings = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("[bold]Setting[/]")
            .AddColumn("[bold]Value[/]");

        settings.AddRow("Discord bot", string.IsNullOrEmpty(config.Discord.BotToken) ? "[red]no token yet[/]"
            : session.BotProblem is not null ? $"[red]{Markup.Escape(session.BotProblem)}[/]"
            : $"[green]{Markup.Escape(session.BotName ?? "token saved")}[/], commands start with {Markup.Escape(config.Discord.CommandPrefix)}");
        settings.AddRow("Transcription", $"Whisper {Markup.Escape(config.Whisper.ModelSize)}, on the {(config.Whisper.GpuDevice >= 0 ? "graphics card" : "processor")}");
        settings.AddRow("Session notes by", Markup.Escape(summaries.Describe(config.Ai)));
        settings.AddRow("Notes folder", string.IsNullOrEmpty(config.Obsidian.VaultPath)
            ? $"[dim]none[/] (summaries go to {Markup.Escape(config.Output.Directory)})"
            : Markup.Escape(config.Obsidian.VaultPath));
        settings.AddRow("Keep transcripts", config.Output.SaveMarkdown ? "yes" : "no");
        settings.AddRow("Post in Discord", config.Output.PostToDiscord ? "yes" : "no");

        console.Write(settings);
    }

    private async Task<int> SaveAsync(AppConfig config)
    {
        if (environment.DryRun)
        {
            console.MarkupLine($"\n[yellow]Dry run: nothing was saved.[/] A real run would have written these settings to {Markup.Escape(configStore.ConfigPath)}");
        }
        else
        {
            await configStore.SaveAsync(config);
            console.MarkupLine($"\n[green]Saved:[/] {Markup.Escape(configStore.ConfigPath)}");
        }

        var promptPath = environment.EnsureInstructionsFile(config.Ai.PromptFile);
        if (promptPath is not null)
            console.MarkupLine($"[dim]How the notes are written is set in[/] {Markup.Escape(promptPath)}");

        if (string.IsNullOrWhiteSpace(config.Discord.BotToken))
        {
            console.MarkupLine("\n[yellow]The bot has no token yet[/], so it can't start. Run [bold]scribewench setup[/] again when you have one.");
            return 0;
        }

        var prefix = Markup.Escape(config.Discord.CommandPrefix);
        console.MarkupLine("\nStart the bot with [bold]scribewench run[/]. Then, in your Discord server:");
        console.MarkupLine($"  [bold]{prefix} create-game <name>[/]   once per campaign");
        console.MarkupLine($"  [bold]{prefix} join <name>[/]          from a voice channel, to start recording");
        console.MarkupLine($"  [bold]{prefix} stop[/]                 to stop and get the notes");
        return 0;
    }
}
