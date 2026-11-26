using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using ScribeWench.AI.Summaries;
using ScribeWench.Core.Models;

namespace ScribeWench.AI.Backends;

// A model served by Ollama, on this machine or the network. Local models have small context
// windows, so every request states the window and is sized to fit it; Ollama would otherwise cut a
// long request short without saying so.
public sealed class OllamaBackend(
    OllamaConfig config,
    IHttpClientFactory factory,
    OllamaClient ollama,
    IOllamaProgram program,
    ILogger<OllamaBackend> logger) : Backend(logger)
{
    // Turned off for a model that rejects the "think" setting.
    private bool _sendThink = true;

    public override string Key => "ollama";
    public override string Label => $"ollama/{config.Model}";
    protected override string Summarizing => $"Ollama model {config.Model}";
    protected override int Window => Math.Max(ChunkedSummarizer.MinContextTokens, config.ContextSize);
    protected override int ThinkingTokens => config.Think ? Math.Clamp(Window / 8, 512, 4096) : 0;

    // A bad address shows when the bot starts, not after the first session.
    public override Task CheckAsync(CancellationToken ct)
    {
        _ = new Uri(config.BaseUrl.TrimEnd('/') + "/");
        return Task.CompletedTask;
    }

    public override Task<SummaryResult> SummarizeAsync(
        IReadOnlyList<TranscriptSegment> transcript, SummarizationOptions options, CancellationToken ct = default)
    {
        if (config.ContextSize < ChunkedSummarizer.MinContextTokens)
            Log.LogWarning("ai.ollama.contextSize is {Configured:N0}; using the smallest that works, {Min:N0}",
                config.ContextSize, ChunkedSummarizer.MinContextTokens);
        return base.SummarizeAsync(transcript, options, ct);
    }

    // Has to fit as it is, such as an edit with all of a game's notes. Fails with an explanation
    // when it doesn't, rather than letting the model work from a request cut short.
    public override async Task<string> CompleteAsync(string systemPrompt, string userMessage, CancellationToken ct = default)
    {
        var maxOutput = Math.Clamp(Window / 4, 1024, 8192) + ThinkingTokens;
        const string advice = "Raise ai.ollama.contextSize in config.json if the machine has the memory for it, or use a backend with a larger window.";

        ChunkedSummarizer.Reply reply;
        try
        {
            reply = await ChatAsync(systemPrompt, userMessage, maxOutput, ct);
        }
        catch (ChunkedSummarizer.RequestTooLongException ex)
        {
            throw new InvalidOperationException($"This is too much for the model's context window ({Window:N0} tokens): {ex.Message} {advice}", ex);
        }

        if (reply.PromptTokens + maxOutput > Window)
            throw new InvalidOperationException(
                $"This is too much for the model's context window: the request is {reply.PromptTokens:N0} tokens, and the window is {Window:N0} with {maxOutput:N0} kept for the reply. {advice}");
        return reply.Text;
    }

    protected override async Task<ChunkedSummarizer.Reply> ChatAsync(string systemPrompt, string userMessage, int maxOutputTokens, CancellationToken ct)
    {
        // Send it. Nothing answering usually means Ollama isn't running: start it.
        var timer = Stopwatch.StartNew();
        int status;
        string body;
        try
        {
            (status, body) = await PostChatAsync(systemPrompt, userMessage, maxOutputTokens, _sendThink, ct);
        }
        catch (HttpRequestException ex)
        {
            if (await ollama.EnsureRunningAsync(config.BaseUrl, ct: ct) is not { } version)
                throw new InvalidOperationException(NotRunningMessage(), ex);
            Log.LogInformation("Ollama {Version} is running at {Url}", version, config.BaseUrl);
            (status, body) = await PostChatAsync(systemPrompt, userMessage, maxOutputTokens, _sendThink, ct);
        }

        // A model that setup didn't download, or that was removed since: get it now.
        if (status == 404 && config.AutoDownload && OllamaClient.ErrorIn(body).Contains("not found", StringComparison.OrdinalIgnoreCase))
        {
            await DownloadModelAsync(ct);
            timer.Restart();
            (status, body) = await PostChatAsync(systemPrompt, userMessage, maxOutputTokens, _sendThink, ct);
        }

        // Models that can't think refuse the setting on some Ollama versions; they don't need it.
        if (status >= 400 && _sendThink && body.Contains("think", StringComparison.OrdinalIgnoreCase))
        {
            _sendThink = false;
            (status, body) = await PostChatAsync(systemPrompt, userMessage, maxOutputTokens, false, ct);
        }

        // What went wrong, if anything did.
        if (status >= 400)
        {
            var error = OllamaClient.ErrorIn(body);
            if (LooksLikeTooLong(error))
                throw new ChunkedSummarizer.RequestTooLongException(error);
            if (status == 404)
                throw new InvalidOperationException(
                    $"Ollama doesn't have the model '{config.Model}'. Download it with `ollama pull {config.Model}`, or run `scribewench setup` and pick a model there.");
            throw new HttpRequestException($"Ollama answered {status} for model '{config.Model}': {error}");
        }

        var response = JsonSerializer.Deserialize<OllamaResponse>(body)
            ?? throw new InvalidOperationException("Ollama sent an empty reply.");
        if (!string.IsNullOrWhiteSpace(response.Error))
            throw new HttpRequestException($"Ollama reported an error for model '{config.Model}': {response.Error}");

        // The reply.
        var text = response.Message?.Content ?? "";
        var cutOff = string.Equals(response.DoneReason, "length", StringComparison.OrdinalIgnoreCase);
        Log.LogInformation("Ollama {Model}: {Prompt:N0} tokens in, {Output:N0} out in {Seconds:N0} s{CutOff}",
            config.Model, response.PromptEvalCount, response.EvalCount, timer.Elapsed.TotalSeconds, cutOff ? " (stopped at the reply limit)" : "");

        if (text.Trim().Length == 0)
            throw new InvalidOperationException(
                cutOff && !string.IsNullOrWhiteSpace(response.Message?.Thinking)
                    ? $"The model '{config.Model}' used up its reply on thinking and wrote nothing. Set ai.ollama.think to false, or raise ai.ollama.contextSize."
                    : $"The model '{config.Model}' replied with nothing.");

        return new ChunkedSummarizer.Reply(text, response.PromptEvalCount, cutOff);
    }

    private async Task<(int Status, string Body)> PostChatAsync(
        string systemPrompt, string userMessage, int maxOutputTokens, bool sendThink, CancellationToken ct)
    {
        var options = new Dictionary<string, object>
        {
            ["num_ctx"] = Window,
            ["num_predict"] = maxOutputTokens,
        };
        if (config.Temperature is { } temperature) options["temperature"] = temperature;

        var request = new Dictionary<string, object?>
        {
            ["model"] = config.Model,
            ["stream"] = false,
            // Refuse a request that doesn't fit instead of quietly dropping the start of it.
            ["truncate"] = false,
            ["messages"] = new[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userMessage },
            },
            ["options"] = options,
        };
        if (sendThink) request["think"] = config.Think;

        var http = factory.CreateClient(OllamaClient.HttpName);
        // Per request. A long session takes several, and without a graphics card each can take a while.
        http.Timeout = TimeSpan.FromMinutes(60);
        using var response = await http.PostAsJsonAsync($"{config.BaseUrl.TrimEnd('/')}/api/chat", request, ct);
        return ((int)response.StatusCode, await response.Content.ReadAsStringAsync(ct));
    }

    private string NotRunningMessage() =>
        OllamaClient.IsLocal(config.BaseUrl)
            ? !program.Installed
                ? $"Ollama isn't installed on this machine. Install it from {OllamaClient.DownloadPage}, or run `scribewench setup` and pick another way to write summaries."
                : $"Ollama is installed but nothing answers at {config.BaseUrl}, and starting it didn't help. Start Ollama and re-run the summary from the saved transcript."
            : $"Nothing answers at {config.BaseUrl} (ai.ollama.baseUrl). Check that Ollama is running on that machine and reachable from this one.";

    private async Task DownloadModelAsync(CancellationToken ct)
    {
        Log.LogWarning("Ollama doesn't have the model '{Model}' yet. Downloading it now; this can take a while", config.Model);
        var lastTenth = -1;
        try
        {
            await ollama.PullAsync(config.BaseUrl, config.Model, progress =>
            {
                if (progress.TotalBytes <= 0) return;
                // The total grows as Ollama learns of the model's files, so the share can step back.
                var tenth = (int)(10 * progress.CompletedBytes / progress.TotalBytes);
                if (tenth <= lastTenth) return;
                lastTenth = tenth;
                Log.LogInformation("Downloading {Model}: {Done:0.0} of {Total:0.0} GB",
                    config.Model, progress.CompletedBytes / 1e9, progress.TotalBytes / 1e9);
            }, ct);
        }
        catch (OllamaException ex)
        {
            throw new InvalidOperationException($"Couldn't download the model '{config.Model}': {ex.Message}", ex);
        }
        Log.LogInformation("Downloaded {Model}", config.Model);
    }

    // Ollama 0.40 says "the prompt is longer than the context length currently available to the
    // model"; the other wordings cover versions that put it differently.
    public static bool LooksLikeTooLong(string error) =>
        error.Contains("context", StringComparison.OrdinalIgnoreCase)
        && (error.Contains("longer than", StringComparison.OrdinalIgnoreCase)
            || error.Contains("too long", StringComparison.OrdinalIgnoreCase)
            || error.Contains("exceed", StringComparison.OrdinalIgnoreCase)
            || error.Contains("truncat", StringComparison.OrdinalIgnoreCase));

    private sealed class OllamaResponse
    {
        [JsonPropertyName("message")]
        public OllamaMessage? Message { get; set; }

        [JsonPropertyName("done_reason")]
        public string? DoneReason { get; set; }

        [JsonPropertyName("prompt_eval_count")]
        public int PromptEvalCount { get; set; }

        [JsonPropertyName("eval_count")]
        public int EvalCount { get; set; }

        [JsonPropertyName("error")]
        public string? Error { get; set; }
    }

    private sealed class OllamaMessage
    {
        [JsonPropertyName("content")]
        public string Content { get; set; } = string.Empty;

        [JsonPropertyName("thinking")]
        public string? Thinking { get; set; }
    }
}
