using System.Text.RegularExpressions;
using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;
using ScribeWench.AI.Summaries;

namespace ScribeWench.Bot.Commands;

// A change to a game's instructions waits for the person who asked to press Confirm. Anyone may
// ask; one request per person, and asking again replaces it.
public sealed class InstructionsConfirmations(InstructionsStore store, TimeProvider time, ILogger<InstructionsConfirmations> logger)
{
    private static readonly TimeSpan ConfirmTimeout = TimeSpan.FromMinutes(5);
    private const string ButtonPrefix = "instructions:";

    // NewText: the uploaded file; null for a reset.
    // Baseline: the game's own file when this was asked (null = defaults), so a change made since isn't overwritten.
    private sealed class Pending(string id, ulong userId, Game game, string? newText, string? baseline, string prompt)
    {
        public string Id { get; } = id;
        public ulong UserId { get; } = userId;
        public Game Game { get; } = game;
        public string? NewText { get; } = newText;
        public string? Baseline { get; } = baseline;
        public string Prompt { get; } = prompt;
        public IUserMessage? Message { get; set; }
    }

    private readonly object _lock = new();
    private readonly Dictionary<ulong, Pending> _pendingByUser = new();

    public async Task AskAsync(CommandRequest request, Game game, string? newText, string prompt, string confirmLabel, ButtonStyle confirmStyle)
    {
        var user = request.User;
        var pending = new Pending(Guid.NewGuid().ToString("N")[..12], user.Id, game, newText, store.ReadOwn(game.Folder), prompt);

        // Registered before the message is sent, so a quick click finds it.
        Pending? replaced;
        lock (_lock)
        {
            _pendingByUser.TryGetValue(user.Id, out replaced);
            _pendingByUser[user.Id] = pending;
        }
        if (replaced is not null) await CloseAsync(replaced, "*Replaced by a newer request. Nothing changed.*");

        var buttons = new ComponentBuilder()
            .WithButton(confirmLabel, $"{ButtonPrefix}{pending.Id}:yes", confirmStyle)
            .WithButton("Cancel", $"{ButtonPrefix}{pending.Id}:no", ButtonStyle.Secondary)
            .Build();
        var who = Format.Sanitize(user.DisplayName ?? user.Username);
        pending.Message = await request.Channel.SendMessageAsync(
            $"{prompt}\n-# Only {who} can confirm. Expires in {ConfirmTimeout.TotalMinutes:0} minutes.",
            components: buttons, allowedMentions: AllowedMentions.None);

        _ = ExpireLaterAsync(pending);
    }

    // A press on Confirm or Cancel. False when the button isn't one of these.
    public async Task<bool> HandleButtonAsync(SocketMessageComponent component)
    {
        var customId = component.Data.CustomId;
        if (!customId.StartsWith(ButtonPrefix, StringComparison.Ordinal)) return false;

        // Whose request it is, and whether it is still open.
        var parts = customId[ButtonPrefix.Length..].Split(':');
        var confirmed = parts.Length == 2 && parts[1] == "yes";

        Pending? pending;
        lock (_lock) pending = _pendingByUser.Values.FirstOrDefault(p => p.Id == parts[0]);

        if (pending is not null && component.User.Id != pending.UserId)
        {
            await component.RespondAsync("Only the person who asked can confirm or cancel this.", ephemeral: true);
            return true;
        }
        if (pending is null || !TryTake(pending))
        {
            await UpdateAsync(component, component.Message.Content, "*No longer open. Nothing changed.*");
            return true;
        }
        if (!confirmed)
        {
            await UpdateAsync(component, pending.Prompt, "*Cancelled. Nothing changed.*");
            return true;
        }

        // Save or reset, unless the file changed under the request.
        var game = pending.Game;
        var name = Format.Sanitize(game.Name);
        try
        {
            if (store.ReadOwn(game.Folder) != pending.Baseline)
            {
                await UpdateAsync(component, pending.Prompt,
                    "*The instructions were changed since this was asked. Nothing changed; run the command again.*");
                return true;
            }

            var (backup, status) = pending.NewText is null
                ? (store.Reset(game.Folder), $"**Reset.** {name}'s next summary uses the default instructions.")
                : (store.Save(game.Folder, pending.NewText), $"**Saved.** {name}'s next summary uses these instructions.");

            logger.LogInformation("{Action} instructions for {Game} by {User} ({Path}); backup {Backup}",
                pending.NewText is null ? "Reset" : "Saved", game.Name, component.User.Username,
                store.PathFor(game.Folder), backup ?? "none");
            await UpdateAsync(component, pending.Prompt, status);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not change the instructions for {Game}", game.Name);
            await UpdateAsync(component, pending.Prompt, $"Couldn't save: {Format.Sanitize(ex.Message)}");
        }
        return true;
    }

    private bool TryTake(Pending pending)
    {
        lock (_lock)
        {
            if (!_pendingByUser.TryGetValue(pending.UserId, out var current) || !ReferenceEquals(current, pending)) return false;
            _pendingByUser.Remove(pending.UserId);
            return true;
        }
    }

    private async Task ExpireLaterAsync(Pending pending)
    {
        await Task.Delay(ConfirmTimeout, time);
        if (TryTake(pending)) await CloseAsync(pending, "*Expired. Nothing changed.*");
    }

    // Takes the buttons off a request's message and says how it ended.
    private async Task CloseAsync(Pending pending, string status)
    {
        if (pending.Message is null) return;
        try
        {
            await pending.Message.ModifyAsync(m =>
            {
                m.Content = $"{pending.Prompt}\n{status}";
                m.Components = new ComponentBuilder().Build();
            });
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not update the confirmation message for {Game}", pending.Game.Name);
        }
    }

    private static Task UpdateAsync(SocketMessageComponent component, string prompt, string status) =>
        component.UpdateAsync(m =>
        {
            // Without the "-# Only … can confirm" line.
            m.Content = $"{Regex.Replace(prompt, @"\n-# Only .*$", "", RegexOptions.Singleline).TrimEnd()}\n{status}";
            m.Components = new ComponentBuilder().Build();
        });
}
