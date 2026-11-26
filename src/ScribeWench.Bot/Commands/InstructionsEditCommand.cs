using System.Text;
using Discord;
using Microsoft.Extensions.Logging;
using ScribeWench.AI.Summaries;

namespace ScribeWench.Bot.Commands;

public sealed class InstructionsEditCommand(
    InstructionsStore store,
    InstructionsConfirmations confirmations,
    AttachmentDownloader downloader,
    GameLookup lookup,
    DiscordConfig discord,
    ILogger<InstructionsEditCommand> logger) : InstructionsCommand(lookup, discord)
{
    public override string Name => "instructions-edit";
    public override string Help => "replace them with the attached .md file";
    protected override string HowToUse => $"{base.HowToUse} with the edited instructions file attached";

    protected override async Task ExecuteAsync(CommandRequest request, Game game)
    {
        var name = Format.Sanitize(game.Name);
        if (request.Attachments.Count != 1)
        {
            await request.ReplyAsync(
                (request.Attachments.Count == 0 ? "Attach the edited instructions file. " : "Attach just one file. ") +
                $"Get the current one with {DiscordText.Code($"{Prefix} instructions-show {game.Name}")}.");
            return;
        }

        // Fetch the file and see that it can be used.
        var attachment = request.Attachments.First();
        var fileName = DiscordText.Code(attachment.Filename);
        Instructions.Review review;
        if (attachment.Size > Instructions.MaxFileBytes)
        {
            review = new Instructions.Review(null, "", [$"It's {attachment.Size / 1000:N0} KB; the most is {Instructions.MaxFileBytes / 1000} KB."]);
        }
        else
        {
            byte[] bytes;
            try
            {
                bytes = await downloader.DownloadAsync(attachment.Url, TimeSpan.FromMinutes(1));
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                logger.LogWarning(ex, "Could not download {File}", attachment.Filename);
                await request.ReplyAsync($"Couldn't download {fileName}. Try again.");
                return;
            }
            review = Instructions.ReviewUpload(attachment.Filename, bytes);
        }

        if (!review.Ok)
        {
            logger.LogInformation("Instructions upload {File} for {Game} rejected: {Problems}",
                attachment.Filename, game.Name, string.Join(" ", review.Problems));
            await request.ReplyAsync(
                $"{fileName} can't be used for **{name}**:\n" +
                string.Join("\n", review.Problems.Select(p => $"- {p}")) +
                "\nNothing was changed.");
            return;
        }

        // What it would change.
        var current = store.Load(game.Folder);
        var changes = Instructions.Compare(current.Instructions, review.Instructions!);
        if (!changes.Any(c => c.Changed))
        {
            await request.ReplyAsync(current.Custom
                ? $"{fileName} is the same as **{name}**'s instructions. Nothing to change."
                : $"{fileName} is the same as the default instructions **{name}** uses. Nothing to change.");
            return;
        }

        // Ask before saving.
        var prompt = new StringBuilder();
        prompt.AppendLine(current.Custom
            ? $"Replace **{name}**'s instructions with {fileName}?"
            : $"Give **{name}** its own instructions from {fileName}? It uses the defaults now.");
        foreach (var change in changes)
            prompt.AppendLine(change.Changed
                ? $"- {change.Section}: +{change.Added} / −{change.Removed} lines"
                : $"- {change.Section}: unchanged");
        if (current.Custom) prompt.AppendLine("The current file is backed up first.");

        await confirmations.AskAsync(request, game, review.Text, prompt.ToString().TrimEnd(), "Save", ButtonStyle.Success);
    }
}
