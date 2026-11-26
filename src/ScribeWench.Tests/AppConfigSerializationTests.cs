using System.Text.Json;
using System.Text.Json.Serialization;
using ScribeWench.Core.Models;

namespace ScribeWench.Tests;

public class AppConfigSerializationTests
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    [Fact]
    public void RoundTrip_DefaultConfig_PreservesValues()
    {
        var original = new AppConfig();
        var json = JsonSerializer.Serialize(original, Options);
        var restored = JsonSerializer.Deserialize<AppConfig>(json, Options);

        Assert.NotNull(restored);
        Assert.Equal(original.Discord.CommandPrefix, restored.Discord.CommandPrefix);
        Assert.Equal(original.Whisper.ModelSize, restored.Whisper.ModelSize);
        Assert.Equal(original.Ai.Backend, restored.Ai.Backend);
        Assert.Equal(original.Output.CampaignName, restored.Output.CampaignName);
    }

    [Fact]
    public void RoundTrip_CustomValues_PreservesAll()
    {
        var original = new AppConfig
        {
            Discord = new DiscordConfig { BotToken = "abc123", CommandPrefix = "!dnd" },
            Whisper = new WhisperConfig { ModelSize = "large", Language = "nl" },
            Ai = new AiConfig { Backend = "anthropic" },
            Output = new OutputConfig { CampaignName = "Curse of Strahd", PostToDiscord = false }
        };

        var json = JsonSerializer.Serialize(original, Options);
        var restored = JsonSerializer.Deserialize<AppConfig>(json, Options)!;

        Assert.Equal("abc123", restored.Discord.BotToken);
        Assert.Equal("!dnd", restored.Discord.CommandPrefix);
        Assert.Equal("large", restored.Whisper.ModelSize);
        Assert.Equal("nl", restored.Whisper.Language);
        Assert.Equal("anthropic", restored.Ai.Backend);
        Assert.Equal("Curse of Strahd", restored.Output.CampaignName);
        Assert.False(restored.Output.PostToDiscord);
    }

    [Fact]
    public void Serialized_UsedCamelCase_Keys()
    {
        var config = new AppConfig();
        var json = JsonSerializer.Serialize(config, Options);

        Assert.Contains("\"discord\"", json);
        Assert.Contains("\"botToken\"", json);
        Assert.Contains("\"commandPrefix\"", json);
        Assert.DoesNotContain("\"Discord\"", json);
        Assert.DoesNotContain("\"BotToken\"", json);
    }
}
