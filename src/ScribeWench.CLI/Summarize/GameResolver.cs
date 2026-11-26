using ScribeWench.AI.Notes;
using ScribeWench.Core;
using ScribeWench.Core.Models;
using Spectre.Console;

namespace ScribeWench.CLI.Summarize;

// Finds the game a session belongs to: the one asked for with --game, or else the one named in the
// transcript's title line.
public sealed class GameResolver(GameStore games, INoteStore notes, IAnsiConsole console)
{
    // Ok is false when the run can't go on; why has then been said.
    public async Task<(Game? Game, bool Ok)> ResolveAsync(string? requested, string? fromTranscript)
    {
        var all = await games.ListAllAsync();
        var gameList = all.Count == 0
            ? "No games exist yet; create one in Discord with [bold]!scribe create-game <name>[/]."
            : "Games: " + string.Join(", ", all.Select(g => Markup.Escape(g.Name)).Distinct(StringComparer.OrdinalIgnoreCase));

        var name = requested ?? fromTranscript;
        if (!string.IsNullOrWhiteSpace(name))
        {
            var normalized = GameStore.NormalizeName(name);
            // The same name on two Discord servers is the same folder, so the first match will do.
            var match = all.FirstOrDefault(g => g.Name.Equals(normalized, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                console.MarkupLine($"[dim]Game:[/] {Markup.Escape(match.Name)}");
                return (match, true);
            }
            if (requested is not null)
            {
                console.MarkupLine($"[red]There's no game called[/] {Markup.Escape(requested)}. {gameList}");
                return (null, false);
            }
        }

        // Without a game it falls back to the configured campaign name, where the notes allow that.
        if (notes.NeedsGame)
        {
            console.MarkupLine($"[red]Which game is this session for?[/] Pass [bold]--game <name>[/]. {gameList}");
            return (null, false);
        }
        return (null, true);
    }
}
