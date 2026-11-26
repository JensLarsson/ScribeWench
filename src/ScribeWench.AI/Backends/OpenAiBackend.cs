using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ScribeWench.AI.Summaries;
using ScribeWench.Core.Models;

namespace ScribeWench.AI.Backends;

// OpenAI, or any service or local server with the same chat API: DeepSeek, OpenRouter, Moonshot,
// LM Studio, llama.cpp and so on. Only the part they all share is used: a model name and two messages.
public sealed class OpenAiBackend(
    OpenAiConfig config,
    IHttpClientFactory factory,
    ILogger<OpenAiBackend> logger,
    TimeSpan? retryDelay = null) : Backend(logger)
{
    public const string HttpName = "openai";
    private const int MaxAttempts = 3;

    // Before the first retry of a request the service was too busy for; later retries wait longer.
    private readonly TimeSpan _retryDelay = retryDelay ?? TimeSpan.FromSeconds(3);

    public override string Key => "openai";
    public override string Label => config.Model;
    protected override string Summarizing => $"{config.Model} at {BaseUrl}";
    protected override int Window => config.ContextSize > 0 ? Math.Max(ChunkedSummarizer.MinContextTokens, config.ContextSize) : 0;

    private string BaseUrl => BaseUrlOf(config.BaseUrl);

    // The address in use: the configured one, or OpenAI's.
    public static string BaseUrlOf(string? configured) =>
        string.IsNullOrWhiteSpace(configured) ? OpenAiConfig.DefaultBaseUrl : configured.Trim().TrimEnd('/');

    public override async Task<string> CompleteAsync(string systemPrompt, string userMessage, CancellationToken ct = default)
    {
        try
        {
            var reply = await ChatAsync(systemPrompt, userMessage, 0, ct);
            if (reply.CutOff)
                Log.LogWarning("The reply from {Model} stopped at the service's length limit and may be incomplete", config.Model);
            return reply.Text;
        }
        catch (ChunkedSummarizer.RequestTooLongException ex)
        {
            throw new InvalidOperationException(
                $"This is too much for '{config.Model}' to read at once: {ex.Message} " +
                "Set ai.openAi.contextSize in config.json to the model's context window so long sessions are read in parts, " +
                "lower obsidian.maxNoteCharacters, or pick a model with a larger window.", ex);
        }
    }

    // The reply limit isn't sent: the services disagree on what it is called, and each enforces its own window.
    protected override async Task<ChunkedSummarizer.Reply> ChatAsync(string systemPrompt, string userMessage, int maxOutputTokens, CancellationToken ct)
    {
        var request = new
        {
            model = config.Model,
            messages = new[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userMessage },
            },
        };

        var http = factory.CreateClient(HttpName);
        // The whole reply comes at once, and a model that reasons first can take minutes over it.
        http.Timeout = TimeSpan.FromMinutes(30);

        var timer = Stopwatch.StartNew();
        for (var attempt = 1; ; attempt++)
        {
            // Send it, trying again when nothing answers.
            int status;
            string body;
            TimeSpan? retryAfter = null;
            try
            {
                using var message = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/chat/completions") { Content = JsonContent.Create(request) };
                Authorize(message, BaseUrl, config.ApiKey);
                using var response = await http.SendAsync(message, ct);
                status = (int)response.StatusCode;
                body = await response.Content.ReadAsStringAsync(ct);
                retryAfter = response.Headers.RetryAfter?.Delta;
            }
            catch (HttpRequestException ex) when (attempt < MaxAttempts)
            {
                Log.LogWarning("No answer from {Url} ({Error}); trying again", BaseUrl, ex.Message);
                await Task.Delay(_retryDelay * attempt, ct);
                continue;
            }
            catch (HttpRequestException ex)
            {
                throw new InvalidOperationException($"Couldn't reach {BaseUrl}: {ex.Message}", ex);
            }

            // Too busy: wait as long as it asks, within reason.
            if (status is 429 or >= 500 && attempt < MaxAttempts)
            {
                var wait = retryAfter is { } asked && asked < TimeSpan.FromMinutes(2) ? asked : _retryDelay * attempt * attempt;
                Log.LogWarning("{Url} answered {Status}; trying again in {Seconds:0} s", BaseUrl, status, wait.TotalSeconds);
                await Task.Delay(wait, ct);
                continue;
            }

            if (status >= 400)
                throw Failure(status, ErrorIn(body), body);

            // The reply.
            var reply = ReadReply(body);
            Log.LogInformation("{Model}: {Prompt:N0} tokens in, {Output:N0} out in {Seconds:N0} s{CutOff}",
                config.Model, reply.PromptTokens, reply.OutputTokens, timer.Elapsed.TotalSeconds, reply.CutOff ? " (stopped at the length limit)" : "");

            if (reply.Text.Trim().Length == 0)
                throw new InvalidOperationException($"The model '{config.Model}' replied with nothing.");
            return new ChunkedSummarizer.Reply(reply.Text, reply.PromptTokens, reply.CutOff);
        }
    }

    private Exception Failure(int status, string error, string body)
    {
        var host = new Uri(BaseUrl).Host;
        // Some services put the reason beside the message instead of in it, so the whole reply is checked.
        if (status == 413 || LooksLikeTooLong(error) || body.Contains("context_length_exceeded", StringComparison.OrdinalIgnoreCase))
            return new ChunkedSummarizer.RequestTooLongException(error);
        return status switch
        {
            401 or 403 => new InvalidOperationException($"{host} rejected the API key ({error}). Run `scribewench setup` and enter a valid key."),
            404 => new InvalidOperationException(
                $"{host} has no model called '{config.Model}', or no chat API at {BaseUrl} ({error}). Run `scribewench setup` and pick a model from the service's list."),
            429 => new InvalidOperationException($"{host} is turning requests away (rate limit or no credit left): {error}"),
            _ => new HttpRequestException($"{host} answered {status} for model '{config.Model}': {error}"),
        };
    }

    internal static void Authorize(HttpRequestMessage message, string baseUrl, string? configuredKey)
    {
        // OpenAI's usual environment variable stands in for a missing key, for OpenAI only.
        var key = !string.IsNullOrWhiteSpace(configuredKey) ? configuredKey.Trim()
            : string.Equals(baseUrl, OpenAiConfig.DefaultBaseUrl, StringComparison.OrdinalIgnoreCase)
                ? Environment.GetEnvironmentVariable("OPENAI_API_KEY")?.Trim() ?? ""
                : "";

        // A local server needs no key, and some refuse a header with an empty one.
        if (key.Length > 0)
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
    }

    private static (string Text, int PromptTokens, int OutputTokens, bool CutOff) ReadReply(string body)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(body); }
        catch (JsonException) { throw new InvalidOperationException($"The service sent something that isn't a chat reply: {Shorten(body)}"); }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new InvalidOperationException($"The service sent something that isn't a chat reply: {Shorten(body)}");
            // Some services report a failure with status 200 and an error in the body.
            if (root.TryGetProperty("error", out _) && !root.TryGetProperty("choices", out _))
                throw new InvalidOperationException($"The service reported an error: {ErrorIn(body)}");

            var text = "";
            var cutOff = false;
            if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0)
            {
                var choice = choices[0];
                cutOff = choice.TryGetProperty("finish_reason", out var reason) && reason.ValueKind == JsonValueKind.String && reason.GetString() == "length";
                if (choice.TryGetProperty("message", out var message) && message.TryGetProperty("content", out var content))
                    text = ContentText(content);
            }

            int promptTokens = 0, outputTokens = 0;
            if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
            {
                if (usage.TryGetProperty("prompt_tokens", out var prompt) && prompt.TryGetInt32(out var p)) promptTokens = p;
                if (usage.TryGetProperty("completion_tokens", out var completion) && completion.TryGetInt32(out var c)) outputTokens = c;
            }
            return (text, promptTokens, outputTokens, cutOff);
        }
    }

    // A plain string from most services, a list of text parts from a few.
    private static string ContentText(JsonElement content) => content.ValueKind switch
    {
        JsonValueKind.String => content.GetString() ?? "",
        JsonValueKind.Array => string.Concat(content.EnumerateArray()
            .Where(part => part.ValueKind == JsonValueKind.Object && part.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
            .Select(part => part.GetProperty("text").GetString())),
        _ => "",
    };

    // The error a service sent, as "message (code)", whichever of the usual shapes it used.
    public static string ErrorIn(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("error", out var error))
            {
                if (error.ValueKind != JsonValueKind.Object) return error.ToString();
                var message = error.TryGetProperty("message", out var m) ? m.ToString() : error.ToString();
                var code = error.TryGetProperty("code", out var c) && c.ValueKind is JsonValueKind.String or JsonValueKind.Number ? c.ToString() : "";
                return code.Length > 0 && !message.Contains(code, StringComparison.OrdinalIgnoreCase) ? $"{message} ({code})" : message;
            }
        }
        catch (JsonException) { }
        return Shorten(body);
    }

    private static string Shorten(string text) => text.Length > 300 ? text[..300] : text;

    // Whether a refusal is about the request not fitting the context window. The services word it differently.
    public static bool LooksLikeTooLong(string error)
    {
        bool Has(string text) => error.Contains(text, StringComparison.OrdinalIgnoreCase);
        return Has("context_length_exceeded")
            || Has("context length")
            || Has("context window")
            || Has("maximum context")
            || Has("too many tokens")
            || Has("too long")
            || Has("reduce the length")
            || (Has("token") && (Has("exceed") || Has("too large")));
    }
}

// ContextLength: the model's context window in tokens when the service says (OpenRouter does); otherwise 0.
public sealed record ServiceModel(string Id, int ContextLength);

// The models an OpenAI-compatible service offers. Asking also shows that the address and the key work.
public sealed class OpenAiModelCatalog(IHttpClientFactory factory)
{
    // Throws InvalidOperationException, saying why, when the service refuses or isn't there.
    public async Task<IReadOnlyList<ServiceModel>> ListAsync(string? baseUrl, string? apiKey, CancellationToken ct = default)
    {
        // Ask.
        var url = OpenAiBackend.BaseUrlOf(baseUrl);
        string body;
        int status;
        try
        {
            var http = factory.CreateClient(OpenAiBackend.HttpName);
            http.Timeout = TimeSpan.FromSeconds(20);
            using var message = new HttpRequestMessage(HttpMethod.Get, $"{url}/models");
            OpenAiBackend.Authorize(message, url, apiKey);
            using var response = await http.SendAsync(message, ct);
            status = (int)response.StatusCode;
            body = await response.Content.ReadAsStringAsync(ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            throw new InvalidOperationException($"Nothing answers at {url}: {ex.Message}", ex);
        }

        if (status is 401 or 403) throw new InvalidOperationException($"The key was rejected: {OpenAiBackend.ErrorIn(body)}");
        if (status >= 400) throw new InvalidOperationException($"{url}/models answered {status}: {OpenAiBackend.ErrorIn(body)}");

        // Read the list: an array, or an object holding one under "data".
        var models = new List<ServiceModel>();
        try
        {
            using var doc = JsonDocument.Parse(body);
            var list = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement
                : doc.RootElement.TryGetProperty("data", out var data) ? data
                : default;
            if (list.ValueKind == JsonValueKind.Array)
                foreach (var item in list.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("id", out var id) || id.GetString() is not { Length: > 0 } name) continue;
                    var context = item.TryGetProperty("context_length", out var length) && length.TryGetInt32(out var tokens) ? tokens : 0;
                    models.Add(new ServiceModel(name, context));
                }
        }
        catch (JsonException)
        {
            throw new InvalidOperationException($"{url}/models didn't send a model list. Is this the address of an OpenAI-compatible API?");
        }
        return models;
    }
}
