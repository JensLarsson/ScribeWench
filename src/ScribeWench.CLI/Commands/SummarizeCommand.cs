using System.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ScribeWench.CLI.Config;
using ScribeWench.CLI.Summarize;
using ScribeWench.Core;
using Spectre.Console;
using Spectre.Console.Cli;

namespace ScribeWench.CLI.Commands;

public sealed class SummarizeCommandSettings : CommandSettings
{
    [CommandArgument(0, "[file]")]
    public string? WavPath { get; set; }

    [CommandOption("--speaker <name>")]
    public string SpeakerName { get; set; } = "Speaker";

    [CommandOption("--no-summary")]
    public bool NoSummary { get; set; }

    [CommandOption("--game <name>")]
    [Description("Game the session belongs to. Defaults to the game named in the transcript's title line.")]
    public string? Game { get; set; }

    [CommandOption("--replace")]
    [Description("Re-run: replace the session's existing note instead of adding a \"(2)\" note. The current note is sent along, so corrections made to it by hand are kept where they make sense.")]
    public bool Replace { get; set; }

    [CommandOption("--config <file>")]
    [Description("Use this config file instead of ~/.scribewench/config.json, for example to try another AI backend or a copy of the notes folder without changing the bot's own setup.")]
    public string? ConfigPath { get; set; }

    [CommandOption("--verbose")]
    [Description("Show what the summary step is doing: how the session is divided up, and how long each request takes.")]
    public bool Verbose { get; set; }
}

public sealed class SummarizeCommand : AsyncCommand<SummarizeCommandSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, SummarizeCommandSettings settings)
    {
        var configStore = new JsonConfigStore(ScribePaths.Default, settings.ConfigPath);
        var config = await configStore.LoadAsync();
        if (config is null)
        {
            AnsiConsole.MarkupLine(settings.ConfigPath is null
                ? "[red]No configuration found.[/] Run [bold]scribewench setup[/] first."
                : $"[red]Config file not found:[/] {Markup.Escape(configStore.ConfigPath)}");
            return 1;
        }
        if (settings.ConfigPath is not null)
            AnsiConsole.MarkupLine($"[dim]Config:[/] {Markup.Escape(configStore.ConfigPath)}");

        await using var provider = new ServiceCollection()
            .AddLogging(logging => ConfigureLogging(logging, settings.Verbose))
            .AddScribeWench(config)
            .AddSummarize()
            .BuildServiceProvider();
        return await provider.GetRequiredService<SummarizeRunner>().RunAsync(settings);
    }

    private static void ConfigureLogging(ILoggingBuilder logging, bool verbose)
    {
        if (!verbose)
        {
            logging.AddConsole().SetMinimumLevel(LogLevel.Warning);
            return;
        }
        // One timestamped line per step, so it reads as a log of the summary being made, down to
        // the notes taken on each part of a long session. The HTTP client's own line for every
        // request would drown that out.
        logging.AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss "; })
            .SetMinimumLevel(LogLevel.Information)
            .AddFilter("ScribeWench.AI", LogLevel.Debug)
            .AddFilter("System.Net.Http", LogLevel.Warning);
    }
}
