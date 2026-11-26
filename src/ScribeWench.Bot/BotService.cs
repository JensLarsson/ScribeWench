using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ScribeWench.AI.Backends;
using ScribeWench.Bot.Commands;
using ScribeWench.Core.Models;

namespace ScribeWench.Bot;

// The Discord bot as a hosted service: signs in, and passes messages and button presses on.
public sealed class BotService(
    DiscordSocketClient client,
    DiscordConfig discord,
    CommandRouter router,
    InstructionsConfirmations confirmations,
    BackendRegistry backends,
    ILogger<BotService> logger) : IHostedService
{
    // What the bot needs from the gateway: its servers, who is in voice, and messages with their text.
    public static DiscordSocketConfig SocketConfig => new()
    {
        GatewayIntents = GatewayIntents.Guilds
            | GatewayIntents.GuildVoiceStates
            | GatewayIntents.GuildMessages
            | GatewayIntents.MessageContent,
        EnableVoiceDaveEncryption = true,
    };

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await backends.Current.CheckAsync(cancellationToken);

        client.Log += OnLog;
        client.Ready += OnReady;
        client.MessageReceived += message => InBackground(() => router.HandleMessageAsync(message), "command handler");
        client.ButtonExecuted += component => InBackground(() => confirmations.HandleButtonAsync(component), "button handler");

        logger.LogInformation("Starting Discord bot...");
        await client.LoginAsync(TokenType.Bot, discord.BotToken);
        await client.StartAsync();
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("Stopping Discord bot...");
        await client.StopAsync();
    }

    // Off the gateway's thread, which mustn't be held up while a command runs.
    private Task InBackground(Func<Task> handle, string what)
    {
        _ = Task.Run(async () =>
        {
            try { await handle(); }
            catch (Exception ex) { logger.LogError(ex, "Unhandled error in {Handler}", what); }
        });
        return Task.CompletedTask;
    }

    private Task OnLog(LogMessage message)
    {
        var level = message.Severity switch
        {
            LogSeverity.Critical => LogLevel.Critical,
            LogSeverity.Error    => LogLevel.Error,
            LogSeverity.Warning  => LogLevel.Warning,
            LogSeverity.Info     => LogLevel.Information,
            LogSeverity.Verbose  => LogLevel.Debug,
            LogSeverity.Debug    => LogLevel.Trace,
            _                    => LogLevel.Information,
        };
        logger.Log(level, message.Exception, "{Source}: {Message}", message.Source, message.Message);
        return Task.CompletedTask;
    }

    private Task OnReady()
    {
        logger.LogInformation("Discord bot ready as {Username}#{Discriminator}",
            client.CurrentUser.Username, client.CurrentUser.Discriminator);
        return Task.CompletedTask;
    }
}

public static class BotServiceCollectionExtensions
{
    public static IServiceCollection AddScribeCommand<TCommand>(this IServiceCollection services)
        where TCommand : class, IScribeCommand =>
        services.AddSingleton<IScribeCommand, TCommand>();
}
