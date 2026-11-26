using Discord;
using ScribeWench.Core;

namespace ScribeWench.Bot.Commands;

public sealed class ListGamesCommand(GameStore games, SessionManager sessions, DiscordConfig discord) : IScribeCommand
{
    public string Name => "list-games";
    public string Usage => "list-games";
    public string Help => "show games";

    public async Task ExecuteAsync(CommandRequest request)
    {
        var all = await games.ListAsync(request.GuildId);
        if (all.Count == 0)
        {
            await request.ReplyAsync($"No games yet. Create one with {DiscordText.Code($"{discord.CommandPrefix} create-game <name>")}.");
            return;
        }

        var current = sessions.CurrentGame;
        var lines = all.Select(g =>
        {
            var count = g.SessionCount == 1 ? "1 session" : $"{g.SessionCount} sessions";
            var last = g.LastSessionAt is { } at ? $", last {at.LocalDateTime:yyyy-MM-dd}" : "";
            var live = GameLookup.Same(current, g.GuildId, g.Name) ? " 🔴" : "";
            return $"• **{Format.Sanitize(g.Name)}** — {count}{last}{live}";
        });
        await request.ReplyAsync($"**Games**\n{string.Join("\n", lines)}");
    }
}
