using System.Text;
using System.Text.RegularExpressions;
using Discord;
using ScribeWench.AI.Summaries;

namespace ScribeWench.Bot.Commands;

public sealed class InstructionsShowCommand(InstructionsStore store, GameLookup lookup, DiscordConfig discord)
    : InstructionsCommand(lookup, discord)
{
    public override string Name => "instructions-show";
    public override string Help => "get the game's summary instructions as a file";

    protected override async Task ExecuteAsync(CommandRequest request, Game game)
    {
        var name = Format.Sanitize(game.Name);
        var own = store.ReadOwn(game.Folder);
        string text, note;

        if (own is not null && Instructions.Parse(own) is { Instructions: null } broken)
        {
            // Edited on disk into something unusable: sent anyway, so it can be fixed.
            text = own;
            note = $"**{name}** has its own instructions, but they can't be used, so the defaults are used for now:\n" +
                   string.Join("\n", broken.Problems.Select(p => $"- {p}"));
        }
        else if (own is not null)
        {
            text = own;
            note = $"**{name}** uses its own instructions.";
        }
        else
        {
            text = store.LoadDefault().ToFile();
            note = $"**{name}** uses the default instructions.";
        }

        note += $"\nTo change them, edit this file and send it back with {DiscordText.Code($"{Prefix} instructions-edit {game.Name}")}.";
        using var stream = new MemoryStream(new UTF8Encoding(false).GetBytes(text));
        await request.Channel.SendFileAsync(stream, FileNameFor(game), note);
    }

    private static string FileNameFor(Game game)
    {
        var stem = Regex.Replace(game.Name, @"[^\w\-]+", "-").Trim('-');
        return stem.Length == 0 ? "instructions.md" : $"{stem}-instructions.md";
    }
}
