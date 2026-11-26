using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace ScribeWench.CLI.Wizard;

public sealed record DiscordBot(string Id, string Name);

// MessageContentIntent: whether the bot may read message text; null when Discord didn't say.
public sealed record DiscordApplication(string Id, bool? MessageContentIntent);

public enum DiscordAnswer
{
    Ok,
    // Discord answered, and doesn't accept the token.
    Rejected,
    // No answer at all: offline, blocked, or Discord is down.
    Unreachable,
}

// The few questions setup asks Discord about a bot, each with only the bot's own token.
public sealed class DiscordApi(IHttpClientFactory factory)
{
    private const string Api = "https://discord.com/api/v10";

    // View Channel, Send Messages, Attach Files (instructions are sent as a file), Read Message
    // History, Send Messages in Threads, and for voice: Connect, Speak, Use Voice Activity.
    internal const long Permissions =
        (1L << 10) | (1L << 11) | (1L << 15) | (1L << 16) | (1L << 38) | (1L << 20) | (1L << 21) | (1L << 25);

    // Application flags. A bot in fewer than 100 servers gets the "limited" one when the switch is on.
    private const long GatewayMessageContent = 1L << 18;
    private const long GatewayMessageContentLimited = 1L << 19;

    public const string ApplicationsUrl = "https://discord.com/developers/applications";

    // The link that adds the bot to a server, with the permissions it needs already ticked.
    public static string InviteUrl(string applicationId) =>
        $"https://discord.com/oauth2/authorize?client_id={applicationId}&permissions={Permissions}&scope=bot";

    // The page where the bot's token and its intents are.
    public static string BotPageUrl(string applicationId) =>
        $"https://discord.com/developers/applications/{applicationId}/bot";

    public async Task<(DiscordAnswer Answer, DiscordBot? Bot)> GetBotAsync(string token, CancellationToken ct)
    {
        var (answer, root) = await GetAsync("users/@me", token, ct);
        if (answer != DiscordAnswer.Ok || root is not { } user) return (answer, null);

        var id = Text(user, "id") ?? "";
        var name = Text(user, "global_name") ?? Text(user, "username") ?? "(unnamed)";
        return (DiscordAnswer.Ok, new DiscordBot(id, name));
    }

    public async Task<DiscordApplication?> GetApplicationAsync(string token, CancellationToken ct)
    {
        var (answer, root) = await GetAsync("applications/@me", token, ct);
        if (answer != DiscordAnswer.Ok || root is not { } application) return null;

        bool? messageContent = application.TryGetProperty("flags", out var flags) && flags.TryGetInt64(out var bits)
            ? (bits & (GatewayMessageContent | GatewayMessageContentLimited)) != 0
            : null;
        return new DiscordApplication(Text(application, "id") ?? "", messageContent);
    }

    // The servers the bot is in; null when Discord didn't say.
    public async Task<IReadOnlyList<string>?> GetServerNamesAsync(string token, CancellationToken ct)
    {
        var (answer, root) = await GetAsync("users/@me/guilds", token, ct);
        if (answer != DiscordAnswer.Ok || root is not { ValueKind: JsonValueKind.Array } servers) return null;

        return servers.EnumerateArray().Select(server => Text(server, "name") ?? "(unnamed server)").ToList();
    }

    private async Task<(DiscordAnswer Answer, JsonElement? Root)> GetAsync(string path, string token, CancellationToken ct)
    {
        try
        {
            var http = factory.CreateClient("discord");
            http.Timeout = TimeSpan.FromSeconds(20);
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{Api}/{path}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bot", token);
            using var response = await http.SendAsync(request, ct);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                return (DiscordAnswer.Rejected, null);
            if (!response.IsSuccessStatusCode)
                return (DiscordAnswer.Unreachable, null);

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            return (DiscordAnswer.Ok, doc.RootElement.Clone());
        }
        catch (FormatException)
        {
            // Not a token at all: characters that can't go in a header.
            return (DiscordAnswer.Rejected, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return (DiscordAnswer.Unreachable, null);
        }
    }

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
