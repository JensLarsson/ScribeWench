using Discord;
using ScribeWench.Core;

namespace ScribeWench.Bot.Commands;

public sealed class RemoveGameCommand(GameStore games, SessionManager sessions, DiscordConfig discord) : IScribeCommand
{
    public string Name => "remove-game";
    public string Usage => "remove-game <name>";
    public string Help => "remove a game (its notes are kept)";

    public async Task ExecuteAsync(CommandRequest request)
    {
        if (request.Argument.Length == 0)
        {
            await request.ReplyAsync($"Usage: {DiscordText.Code($"{discord.CommandPrefix} remove-game <name>")}");
            return;
        }

        var current = sessions.CurrentGame;
        if (GameLookup.Same(current, request.GuildId, request.Argument))
        {
            await request.ReplyAsync($"**{Format.Sanitize(current!.Name)}** is in use. Try again after the session.");
            return;
        }

        var removed = await games.RemoveAsync(request.GuildId, request.Argument);
        await request.ReplyAsync(removed is null
            ? $"No game called **{Format.Sanitize(request.Argument)}**."
            : $"Removed **{Format.Sanitize(removed.Name)}**. Its notes are kept.");
    }
}
