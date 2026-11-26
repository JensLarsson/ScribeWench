using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ScribeWench.Core.Models;

namespace ScribeWench.AI.Backends;

// The installed Claude Code program, with whatever account it is signed in to: your own Claude
// subscription, no API key.
public sealed class ClaudeCodeBackend(ClaudeCodeConfig config, ClaudeCli cli, ILogger<ClaudeCodeBackend> logger) : Backend(logger)
{
    public override string Key => "claudecode";
    public override string Label => $"Claude {config.Model} (Claude Code)";
    protected override string Summarizing => $"Claude Code CLI, model {config.Model}, effort {config.Effort}";

    public override async Task CheckAsync(CancellationToken ct)
    {
        var account = await cli.GetAccountAsync(config.ExecutablePath, ct);
        if (account is null)
            Log.LogWarning("Claude Code ('{Executable}') couldn't be run, so summaries will fail. Install it and sign in, or fix ai.claudeCode.executablePath.", config.ExecutablePath);
        else if (account.UsesSubscription)
            Log.LogInformation("{Account}", account.Describe());
        else
            Log.LogWarning("{Account}", account.Describe());
    }

    public override async Task<string> CompleteAsync(string systemPrompt, string userMessage, CancellationToken ct = default)
    {
        // In an empty folder, so no project's CLAUDE.md or settings get picked up.
        var workDir = Path.Combine(Path.GetTempPath(), "scribewench-claude", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);
        var systemPromptPath = Path.Combine(workDir, "instructions.md");
        await File.WriteAllTextAsync(systemPromptPath, systemPrompt, new UTF8Encoding(false), ct);

        try
        {
            return ReadResult(await cli.PromptAsync(config, workDir, systemPromptPath, userMessage, ct));
        }
        finally
        {
            try { Directory.Delete(workDir, recursive: true); } catch { }
        }
    }

    // The reply text out of `--output-format json`, with the program's failures as readable errors.
    private string ReadResult(string stdout)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(stdout);
        }
        catch (JsonException)
        {
            throw new InvalidOperationException($"Claude Code returned something that isn't JSON: {Truncate(stdout)}");
        }

        using (doc)
        {
            var root = doc.RootElement;
            var result = root.TryGetProperty("result", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() ?? "" : "";
            var isError = root.TryGetProperty("is_error", out var e) && e.ValueKind == JsonValueKind.True;
            var stopReason = root.TryGetProperty("stop_reason", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;

            if (root.TryGetProperty("usage", out var usage))
                Log.LogInformation("Claude Code usage: {Input} input + {CacheRead} cached input tokens, {Output} output tokens",
                    GetLong(usage, "input_tokens") + GetLong(usage, "cache_creation_input_tokens"),
                    GetLong(usage, "cache_read_input_tokens"),
                    GetLong(usage, "output_tokens"));

            if (isError)
                throw new InvalidOperationException(
                    $"Claude Code reported an error: {Truncate(result)}. If it says you're not logged in, run `claude` once and log in.");
            if (stopReason == "refusal")
                throw new InvalidOperationException(
                    "Claude declined to summarize this transcript. The transcript is saved, so you can retry later or with a different model.");
            if (string.IsNullOrWhiteSpace(result))
                throw new InvalidOperationException("Claude Code returned an empty summary.");

            if (stopReason == "max_tokens")
                result += "\n\n> *This summary was cut off at the output limit.*";
            return result;
        }
    }

    private static long GetLong(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.TryGetInt64(out var n) ? n : 0;

    private static string Truncate(string text) => text.Length <= 500 ? text.Trim() : text[..500].Trim() + "…";
}
