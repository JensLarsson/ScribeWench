using Discord;
using Discord.WebSocket;

namespace ScribeWench.Bot.Commands;

// What someone typed after the prefix and the command's name, and where to answer.
public sealed record CommandRequest(
    SocketGuildUser User,
    ISocketMessageChannel Channel,
    string Argument,
    IReadOnlyCollection<Attachment> Attachments)
{
    public ulong GuildId => User.Guild.Id;

    public Task ReplyAsync(string text) => Channel.SendMessageAsync(text);
}

/// <summary>
/// Strategy for one "!scribe &lt;name&gt;" command. <see cref="CommandRouter"/> picks it by name and
/// lists them in help in the order they are registered. Replies are kept short; what only matters to
/// whoever runs the bot goes to the log.
/// </summary>
public interface IScribeCommand
{
    /// <summary>What is typed after the prefix: "join".</summary>
    string Name { get; }

    /// <summary>The name with what it takes, for help: "join &lt;game&gt;".</summary>
    string Usage { get; }

    /// <summary>What it does, in a few words.</summary>
    string Help { get; }

    Task ExecuteAsync(CommandRequest request);
}
