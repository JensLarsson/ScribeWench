using Discord;
using ScribeWench.AI.Summaries;

namespace ScribeWench.Bot.Commands;

public sealed class InstructionsResetCommand(
    InstructionsStore store,
    InstructionsConfirmations confirmations,
    GameLookup lookup,
    DiscordConfig discord) : InstructionsCommand(lookup, discord)
{
    public override string Name => "instructions-reset";
    public override string Help => "go back to the default instructions";

    protected override async Task ExecuteAsync(CommandRequest request, Game game)
    {
        var name = Format.Sanitize(game.Name);
        if (store.ReadOwn(game.Folder) is null)
        {
            await request.ReplyAsync($"**{name}** already uses the default instructions.");
            return;
        }

        await confirmations.AskAsync(request, game, null,
            $"Reset **{name}** to the default instructions? Its own file is backed up first.", "Reset", ButtonStyle.Danger);
    }
}
