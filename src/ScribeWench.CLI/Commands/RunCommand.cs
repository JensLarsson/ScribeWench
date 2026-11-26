using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ScribeWench.CLI.Config;
using ScribeWench.Core;
using Spectre.Console;
using Spectre.Console.Cli;

namespace ScribeWench.CLI.Commands;

public sealed class RunCommand : AsyncCommand
{
    public override async Task<int> ExecuteAsync(CommandContext context)
    {
        var config = await new JsonConfigStore(ScribePaths.Default).LoadAsync();
        if (config is null)
        {
            AnsiConsole.MarkupLine("[red]No configuration found.[/] Run [bold]scribewench setup[/] first.");
            return 1;
        }
        if (string.IsNullOrWhiteSpace(config.Discord.BotToken))
        {
            AnsiConsole.MarkupLine("[red]Discord bot token is not set.[/] Run [bold]scribewench setup[/] first.");
            return 1;
        }

        var host = Host.CreateDefaultBuilder()
            .ConfigureLogging(logging =>
            {
                logging.ClearProviders();
                logging.AddConsole();
                logging.SetMinimumLevel(LogLevel.Information);
            })
            .ConfigureServices(services => services.AddScribeWench(config).AddDiscordBot())
            .Build();

        AnsiConsole.MarkupLine("[green]ScribeWench started![/] Press Ctrl+C to stop.");
        AnsiConsole.MarkupLine($"Bot prefix: [bold]{config.Discord.CommandPrefix}[/]");

        await host.RunAsync();
        return 0;
    }
}
