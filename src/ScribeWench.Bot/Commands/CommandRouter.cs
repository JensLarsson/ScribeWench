using Discord.WebSocket;
using Microsoft.Extensions.Logging;
using ScribeWench.Core.Models;

namespace ScribeWench.Bot.Commands;

// Reads a message as a command and hands it to the command strategy of that name.
public sealed class CommandRouter(IEnumerable<IScribeCommand> commands, DiscordConfig discord, ILogger<CommandRouter> logger)
{
    public async Task HandleMessageAsync(SocketMessage message)
    {
        if (message.Author is not SocketGuildUser user || message.Author.IsBot) return;
        if (message.Channel is null || message.Content is null) return;
        if (!TryParse(message.Content, discord.CommandPrefix, out var name, out var argument)) return;

        logger.LogInformation("Command: '{Command}' '{Argument}' from {User}", name, argument, message.Author.Username);
        var request = new CommandRequest(user, message.Channel, argument, message.Attachments);

        if (name is "help" or "")
            await request.ReplyAsync(HelpText());
        else if (commands.FirstOrDefault(c => c.Name == name) is { } command)
            await command.ExecuteAsync(request);
        else
            await request.ReplyAsync($"Unknown command. Try `{discord.CommandPrefix} help`.");
    }

    // "!scribe join Curse of Strahd" is command "join" with argument "Curse of Strahd". The prefix
    // has to be followed by a space or the end of the message, so "!scribes" isn't a command.
    // Quotes around the argument are dropped.
    public static bool TryParse(string content, string prefix, out string command, out string argument)
    {
        command = argument = string.Empty;
        var text = content.Trim();
        if (!text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;

        var rest = text[prefix.Length..];
        if (rest.Length > 0 && !char.IsWhiteSpace(rest[0])) return false;
        rest = rest.Trim();

        var split = rest.IndexOfAny([' ', '\t', '\r', '\n']);
        command = (split < 0 ? rest : rest[..split]).ToLowerInvariant();
        argument = split < 0 ? string.Empty : rest[(split + 1)..].Trim().Trim('"', '“', '”').Trim();
        return true;
    }

    public string HelpText() =>
        "**ScribeWench**\n" + string.Join("\n", commands.Select(c => $"`{discord.CommandPrefix} {c.Usage}` — {c.Help}"));
}
