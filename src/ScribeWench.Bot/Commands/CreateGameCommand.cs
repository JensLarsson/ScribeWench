using Discord;
using Microsoft.Extensions.Logging;
using ScribeWench.AI.Notes;
using ScribeWench.Core;

namespace ScribeWench.Bot.Commands;

public sealed class CreateGameCommand(GameStore games, INoteStore notes, DiscordConfig discord, ILogger<CreateGameCommand> logger) : IScribeCommand
{
    public string Name => "create-game";
    public string Usage => "create-game <name>";
    public string Help => "add a campaign or one-shot";

    public async Task ExecuteAsync(CommandRequest request)
    {
        if (request.Argument.Length == 0)
        {
            await request.ReplyAsync($"Usage: {DiscordText.Code($"{discord.CommandPrefix} create-game <name>")}");
            return;
        }

        var (game, error) = await games.CreateAsync(request.GuildId, request.Argument, request.User.Id);
        if (game is null)
        {
            await request.ReplyAsync(Format.Sanitize(error ?? "Couldn't create the game."));
            return;
        }

        // Its folder in the notes, where there are notes.
        var safeName = Format.Sanitize(game.Name);
        var start = $"Start with {DiscordText.Code($"{discord.CommandPrefix} join {game.Name}")}.";
        try
        {
            if (notes.PrepareGame(game.Folder) is { } folder)
            {
                logger.LogInformation("Game {Game} uses notes folder {Path} ({Count} notes)", game.Name, folder.Path, folder.NoteCount);
                await request.ReplyAsync(folder.Existed
                    ? $"Created **{safeName}**, using its existing notes. {start}"
                    : $"Created **{safeName}**. {start}");
                return;
            }
        }
        catch (Exception ex)
        {
            // Said in the reply: the admin has to fix it (a bad notes folder path, permissions).
            logger.LogWarning(ex, "Could not prepare the notes folder for {Game}", game.Name);
            await request.ReplyAsync($"Created **{safeName}**, but its notes folder couldn't be made: {Format.Sanitize(ex.Message)}");
            return;
        }

        await request.ReplyAsync($"Created **{safeName}**. {start}");
    }
}
