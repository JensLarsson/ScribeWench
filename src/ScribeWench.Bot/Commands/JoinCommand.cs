using Discord;
using ScribeWench.Core;

namespace ScribeWench.Bot.Commands;

public sealed class JoinCommand(GameStore games, GameLookup lookup, SessionManager sessions, DiscordConfig discord) : IScribeCommand
{
    public string Name => "join";
    public string Usage => "join <game>";
    public string Help => "start recording";

    public async Task ExecuteAsync(CommandRequest request)
    {
        if (request.Argument.Length == 0)
        {
            await request.ReplyAsync(
                $"Which game? {DiscordText.Code($"{discord.CommandPrefix} join <game>")}\n{await lookup.ListLineAsync(request.GuildId)}");
            return;
        }

        var game = await games.FindAsync(request.GuildId, request.Argument);
        if (game is null)
        {
            await request.ReplyAsync(
                $"No game called **{Format.Sanitize(request.Argument)}**.\n{await lookup.ListLineAsync(request.GuildId)}");
            return;
        }

        await sessions.JoinAndStartAsync(request.User, request.Channel, game);
    }
}
