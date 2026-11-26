using System.Text.Json;
using System.Text.Json.Serialization;
using ScribeWench.Core;
using ScribeWench.Core.Interfaces;
using ScribeWench.Core.Models;

namespace ScribeWench.CLI.Config;

// path: a config file other than the usual one, for trying out settings without touching it.
public sealed class JsonConfigStore(ScribePaths paths, string? path = null) : IConfigStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public string ConfigPath { get; } =
        string.IsNullOrWhiteSpace(path) ? paths.ConfigFile : PathHelper.Expand(path.Trim().Trim('"'));

    public async Task<AppConfig?> LoadAsync()
    {
        if (!File.Exists(ConfigPath)) return null;
        await using var stream = File.OpenRead(ConfigPath);
        return await JsonSerializer.DeserializeAsync<AppConfig>(stream, JsonOptions);
    }

    public async Task SaveAsync(AppConfig config)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
        await using var stream = File.Create(ConfigPath);
        await JsonSerializer.SerializeAsync(stream, config, JsonOptions);
    }
}
