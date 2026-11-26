using System.Text.Json;
using System.Text.RegularExpressions;
using ScribeWench.Core.Models;

namespace ScribeWench.Core;

// The games, per Discord server, in a JSON file. A game's name is also its folder name.
public sealed class GameStore(ScribePaths paths, TimeProvider time)
{
    public const int MaxNameLength = 60;

    // Not allowed in Windows folder names or in Obsidian links.
    private static readonly char[] ForbiddenNameChars =
        [.. Path.GetInvalidFileNameChars(), '[', ']', '#', '^', '|'];

    private static readonly HashSet<string> ReservedWindowsNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly SemaphoreSlim _lock = new(1, 1);

    public string FilePath => paths.GamesFile;

    // "Curse  of Strahd " and "Curse of Strahd" are the same game.
    public static string NormalizeName(string name) => Regex.Replace(name.Trim(), @"\s+", " ");

    // Why a name can't be used, in one line; null when it can.
    public static string? ValidateName(string name)
    {
        name = NormalizeName(name);
        if (name.Length == 0) return "Needs a name.";
        if (name.Length > MaxNameLength) return $"Too long (max {MaxNameLength} characters).";

        var bad = name.Where(c => ForbiddenNameChars.Contains(c) && !char.IsControl(c)).Distinct().ToArray();
        if (bad.Length > 0) return $"Can't contain {string.Join(" ", bad)}";
        if (name.Any(char.IsControl)) return "Can't contain line breaks or tabs.";

        if (name.StartsWith('.') || name.EndsWith('.')) return "Can't start or end with a dot.";
        if (!name.Any(char.IsLetterOrDigit)) return "Needs at least one letter or number.";
        if (ReservedWindowsNames.Contains(name)) return $"\"{name}\" isn't allowed as a name.";
        return null;
    }

    public async Task<IReadOnlyList<Game>> ListAsync(ulong guildId) =>
        (await ReadAsync()).Where(g => g.GuildId == guildId)
            .OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    public async Task<IReadOnlyList<Game>> ListAllAsync() =>
        (await ReadAsync()).OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase).ToList();

    public async Task<Game?> FindAsync(ulong guildId, string name) =>
        Find(await ReadAsync(), guildId, NormalizeName(name));

    public async Task<(Game? Game, string? Error)> CreateAsync(ulong guildId, string name, ulong createdByUserId)
    {
        name = NormalizeName(name);
        if (ValidateName(name) is { } error) return (null, error);

        await _lock.WaitAsync();
        try
        {
            var games = await LoadAsync();
            if (Find(games, guildId, name) is { } existing)
                return (null, $"{existing.Name} already exists.");

            var game = new Game
            {
                Name = name,
                GuildId = guildId,
                Folder = name,
                CreatedByUserId = createdByUserId,
                CreatedAt = time.GetUtcNow(),
            };
            games.Add(game);
            await SaveAsync(games);
            return (game, null);
        }
        finally
        {
            _lock.Release();
        }
    }

    // Takes the game off the list. Its folder and notes are never touched.
    public async Task<Game?> RemoveAsync(ulong guildId, string name)
    {
        await _lock.WaitAsync();
        try
        {
            var games = await LoadAsync();
            if (Find(games, guildId, NormalizeName(name)) is not { } game) return null;

            games.Remove(game);
            await SaveAsync(games);
            return game;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task RecordSessionAsync(ulong guildId, string name, DateTimeOffset startedAt)
    {
        await _lock.WaitAsync();
        try
        {
            var games = await LoadAsync();
            // Removed mid-session: nothing to update.
            if (Find(games, guildId, name) is not { } game) return;

            game.SessionCount++;
            game.LastSessionAt = startedAt;
            await SaveAsync(games);
        }
        finally
        {
            _lock.Release();
        }
    }

    private static Game? Find(List<Game> games, ulong guildId, string name) =>
        games.FirstOrDefault(g => g.GuildId == guildId && g.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    private async Task<List<Game>> ReadAsync()
    {
        await _lock.WaitAsync();
        try { return await LoadAsync(); }
        finally { _lock.Release(); }
    }

    private async Task<List<Game>> LoadAsync()
    {
        if (!File.Exists(FilePath)) return [];
        await using var stream = File.OpenRead(FilePath);
        return await JsonSerializer.DeserializeAsync<List<Game>>(stream, JsonOptions) ?? [];
    }

    // Through a temp file, so a crash mid-write can't leave half a game list.
    private async Task SaveAsync(List<Game> games)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var tempPath = FilePath + ".tmp";
        await using (var stream = File.Create(tempPath))
            await JsonSerializer.SerializeAsync(stream, games, JsonOptions);
        File.Move(tempPath, FilePath, overwrite: true);
    }
}
