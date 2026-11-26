using System.Reflection;
using ScribeWench.CLI;
using ScribeWench.CLI.Commands;
using ScribeWench.CLI.Config;
using ScribeWench.Core;
using Spectre.Console;
using Spectre.Console.Cli;

UnpackedFiles.Hold();

var app = new CommandApp();
app.Configure(config =>
{
    config.SetApplicationName("scribewench");
    config.SetApplicationVersion(BuildVersion());

    config.AddCommand<SetupCommand>("setup")
        .WithDescription("Interactive setup: the Discord bot, the Whisper model, who writes the session notes, and where they go.")
        .WithExample("setup")
        .WithExample("setup", "--config", "test-config.json");

    config.AddCommand<RunCommand>("run")
        .WithDescription("Start the ScribeWench Discord bot.");

    config.AddCommand<SummarizeCommand>("summarize")
        .WithDescription("Transcribe and summarize an audio file. Saves transcript + summary to your output directory.")
        .WithExample("summarize", "recording.wav")
        .WithExample("summarize", "recording.wav", "--speaker", "Alice")
        .WithExample("summarize", "recording.wav", "--no-summary")
        .WithExample("summarize", "transcript.md", "--game", "Curse of Strahd");
});

if (args.Length > 0)
    return await app.RunAsync(args);

// Nothing after the name, which is what a double-click does: run the bot, or set it up first.
if (await IsConfiguredAsync())
    return await app.RunAsync(["run"]);

var result = await app.RunAsync(["setup"]);
if (result == 0 && await IsConfiguredAsync())
{
    AnsiConsole.WriteLine();
    if (!AnsiConsole.Confirm("Start the bot now?", defaultValue: true))
        return 0;
    result = await app.RunAsync(["run"]);
    if (result == 0) return 0;
}

// The window closes when the program ends, so what a setup someone sat through said is left on
// screen. Only here: a bot restarted by a script must never wait for a key.
if (!Console.IsInputRedirected)
{
    AnsiConsole.WriteLine();
    AnsiConsole.MarkupLine("[dim]Press Enter to close.[/]");
    Console.ReadLine();
}
return result;

// The build's version without the commit the SDK adds after a plus sign.
static string BuildVersion() =>
    typeof(SetupCommand).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "unknown";

static async Task<bool> IsConfiguredAsync()
{
    var config = await new JsonConfigStore(ScribePaths.Default).LoadAsync();
    return config is not null && !string.IsNullOrWhiteSpace(config.Discord.BotToken);
}
