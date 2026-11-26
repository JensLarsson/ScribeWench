using Discord;
using ScribeWench.Core;
using ScribeWench.Core.Models;

namespace ScribeWench.Bot.Commands;

// Finding the game a command is about, and listing the games when it can't be told.
public sealed class GameLookup(GameStore games, DiscordConfig discord)
{
    // "Curse of Strahd Strahd is a vampire" is the game and the rest, going by the longest game
    // name that fits. A server with a single game may leave its name out.
    public async Task<(Game? Game, string Remainder)> SplitAsync(ulong guildId, string argument)
    {
        var all = await games.ListAsync(guildId);
        var match = all
            .Where(g => argument.StartsWith(g.Name, StringComparison.OrdinalIgnoreCase)
                     && (argument.Length == g.Name.Length || char.IsWhiteSpace(argument[g.Name.Length])))
            .MaxBy(g => g.Name.Length);
        if (match is not null) return (match, argument[match.Name.Length..].Trim());
        return all.Count == 1 ? (all[0], argument.Trim()) : (null, argument.Trim());
    }

    // "Games: **A**, **B**", or how to create the first one.
    public async Task<string> ListLineAsync(ulong guildId)
    {
        var all = await games.ListAsync(guildId);
        return all.Count == 0
            ? $"No games yet. Create one with {DiscordText.Code($"{discord.CommandPrefix} create-game <name>")}."
            : "Games: " + string.Join(", ", all.Select(g => $"**{Format.Sanitize(g.Name)}**"));
    }

    public static bool Same(Game? game, ulong guildId, string name) =>
        game is not null && game.GuildId == guildId
        && game.Name.Equals(GameStore.NormalizeName(name), StringComparison.OrdinalIgnoreCase);
}
