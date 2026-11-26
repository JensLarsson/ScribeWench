using Discord;
using Microsoft.Extensions.Logging;
using ScribeWench.AI.Backends;
using ScribeWench.AI.Notes;
using ScribeWench.Core.Models;

namespace ScribeWench.Bot.Commands;

// "!scribe edit Runeterra Orfelt made the portal": has the AI change the game's notes to match.
public sealed class EditCommand(
    GameLookup lookup,
    SessionManager sessions,
    INoteStore notes,
    BackendRegistry backends,
    AttachmentDownloader downloader,
    DiscordConfig discord,
    ILogger<EditCommand> logger) : IScribeCommand
{
    private const long MaxAttachmentBytes = 25L * 1024 * 1024;

    // One edit at a time: two reading the same notes would overwrite each other's changes.
    private readonly SemaphoreSlim _lock = new(1, 1);

    public string Name => "edit";
    public string Usage => "edit <game> <what to change>";
    public string Help => "correct the game's notes, in your own words";

    public async Task ExecuteAsync(CommandRequest request)
    {
        // Which game, and what to change. Files with no words: take in what they hold.
        var (game, instruction) = await lookup.SplitAsync(request.GuildId, request.Argument);
        if (instruction.Length == 0 && request.Attachments.Count > 0)
            instruction = "Add what the attached files contain to the notes.";
        if (game is null || instruction.Length == 0)
        {
            var prefix = discord.CommandPrefix;
            await request.ReplyAsync(
                $"Usage: {DiscordText.Code($"{prefix} edit <game> <what to change>")}, for example " +
                $"{DiscordText.Code($"{prefix} edit Runeterra Orfelt made the portal in the castle, he didn't just open it")}\n" +
                await lookup.ListLineAsync(request.GuildId));
            return;
        }

        // Whether it can be done now.
        if ((sessions.IsFinishing || sessions.IsProcessing) && GameLookup.Same(sessions.CurrentGame, game.GuildId, game.Name))
        {
            await request.ReplyAsync($"Still writing the summary for **{Format.Sanitize(game.Name)}**. Try again when it's posted.");
            return;
        }
        if (!notes.CanEdit)
        {
            await request.ReplyAsync("Editing needs the notes folder, which is turned off in this bot's setup.");
            return;
        }
        if (!await _lock.WaitAsync(0))
        {
            await request.ReplyAsync("Already making an edit. Try again when it's done.");
            return;
        }

        // The edit.
        try
        {
            logger.LogInformation("Editing {Game} for {User}: {Instruction} ({Files} file(s) attached)",
                game.Name, request.User.Username, instruction, request.Attachments.Count);

            NoteEdits.Outcome outcome;
            using (request.Channel.EnterTypingState())
            {
                var edit = new NoteEdits.Request(
                    game.Folder, game.Name, instruction, request.User.DisplayName ?? request.User.Username,
                    await DownloadAsync(request.Attachments));
                outcome = await notes.EditAsync(edit, backends.Current, CancellationToken.None);
            }

            logger.LogInformation("Edit of {Game}: changed {Notes}; not applied: {Failed}; images {Images}; backup {Backup}",
                game.Name, string.Join(", ", outcome.ChangedNotes), string.Join("; ", outcome.Failed),
                string.Join(", ", outcome.SavedImages), outcome.BackupFolder ?? "none");
            await request.ReplyAsync(NoteEdits.FormatForDiscord(outcome));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Edit of {Game} failed", game.Name);
            await request.ReplyAsync($"Couldn't edit the notes: {Format.Sanitize(ex.Message)}");
        }
        finally
        {
            _lock.Release();
        }
    }

    // One that is too big or fails to download is reported as skipped.
    private async Task<List<EditAttachments.File>> DownloadAsync(IReadOnlyCollection<Attachment> attachments)
    {
        var files = new List<EditAttachments.File>();
        foreach (var attachment in attachments)
        {
            if (attachment.Size > MaxAttachmentBytes)
            {
                files.Add(new EditAttachments.File(attachment.Filename, [], "over 25 MB"));
                continue;
            }
            try
            {
                files.Add(new EditAttachments.File(attachment.Filename, await downloader.DownloadAsync(attachment.Url, TimeSpan.FromMinutes(2))));
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                logger.LogWarning(ex, "Could not download {File}", attachment.Filename);
                files.Add(new EditAttachments.File(attachment.Filename, [], "couldn't download it"));
            }
        }
        return files;
    }
}
