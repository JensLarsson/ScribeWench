using Discord;

namespace ScribeWench.Bot.Commands;

// What instructions-show, -edit and -reset share: each takes a game and nothing else.
public abstract class InstructionsCommand(GameLookup lookup, DiscordConfig discord) : IScribeCommand
{
    public abstract string Name { get; }
    public abstract string Help { get; }
    public string Usage => $"{Name} <game>";

    protected string Prefix => discord.CommandPrefix;

    // What to type, said when the game can't be told.
    protected virtual string HowToUse => DiscordText.Code($"{Prefix} {Name} <game>");

    protected abstract Task ExecuteAsync(CommandRequest request, Game game);

    public async Task ExecuteAsync(CommandRequest request)
    {
        var (game, rest) = await lookup.SplitAsync(request.GuildId, request.Argument);
        if (game is null || rest.Length > 0)
        {
            var notFound = request.Argument.Length > 0 ? $"No game called **{Format.Sanitize(request.Argument)}**. " : "";
            await request.ReplyAsync($"{notFound}Usage: {HowToUse}\n{await lookup.ListLineAsync(request.GuildId)}");
            return;
        }
        await ExecuteAsync(request, game);
    }
}
