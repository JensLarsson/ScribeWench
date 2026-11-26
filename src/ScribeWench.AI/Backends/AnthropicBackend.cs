using System.Text;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Beta;
using Anthropic.Models.Beta.Messages;
using Microsoft.Extensions.Logging;
using ScribeWench.Core.Models;

namespace ScribeWench.AI.Backends;

// The Claude API. A four-hour session is well inside the model's context window, so the whole
// transcript goes in one request.
public sealed class AnthropicBackend(AnthropicConfig config, ILogger<AnthropicBackend> logger) : Backend(logger)
{
    // Thinking counts against this as well as the summary.
    private const int MaxOutputTokens = 64000;

    private AnthropicClient? _client;

    public override string Key => "anthropic";
    public override string Label => config.Model;
    protected override string Summarizing => $"Anthropic model {config.Model}";

    // With no key in the config, the SDK reads the ANTHROPIC_API_KEY environment variable.
    private AnthropicClient Client => _client ??= string.IsNullOrWhiteSpace(config.ApiKey)
        ? new AnthropicClient()
        : new AnthropicClient { ApiKey = config.ApiKey };

    public override async Task<string> CompleteAsync(string systemPrompt, string userMessage, CancellationToken ct = default)
    {
        // If a safety classifier declines the request, the API re-runs it on Anthropic's fallback
        // model instead of returning nothing.
        var useFallback = SupportsServerSideFallback(config.Model);
        var parameters = new MessageCreateParams
        {
            Model = config.Model,
            MaxTokens = MaxOutputTokens,
            System = systemPrompt,
            OutputConfig = new BetaOutputConfig { Effort = Effort.High },
            Messages = [new BetaUserMessageParam { Content = userMessage }],
            Betas = useFallback ? [AnthropicBeta.ServerSideFallback2026_07_01] : null,
            Fallbacks = useFallback ? (BetaFallbacksParam)new Default() : null,
        };

        // Streamed, so a long thinking phase can't run into an HTTP timeout.
        var markdown = new StringBuilder();
        string? stopReason = null;
        long inputTokens = 0, outputTokens = 0;
        try
        {
            await foreach (var streamEvent in Client.Beta.Messages.CreateStreaming(parameters, cancellationToken: ct))
            {
                if (streamEvent.TryPickStart(out var start))
                {
                    inputTokens = start.Message.Usage.InputTokens;
                }
                else if (streamEvent.TryPickContentBlockDelta(out var blockDelta))
                {
                    if (blockDelta.Delta.TryPickText(out var textDelta))
                        markdown.Append(textDelta.Text);
                }
                else if (streamEvent.TryPickDelta(out var messageDelta))
                {
                    stopReason = messageDelta.Delta.StopReason?.Raw();
                    outputTokens = messageDelta.Usage.OutputTokens;
                }
            }
        }
        catch (AnthropicUnauthorizedException ex)
        {
            throw new InvalidOperationException(
                "The Anthropic API key was rejected. Run `scribewench setup` and paste a valid key.", ex);
        }
        catch (AnthropicNotFoundException ex)
        {
            throw new InvalidOperationException(
                $"The Claude model '{config.Model}' is not available (it may have been retired). " +
                "Run `scribewench setup` and pick a current model.", ex);
        }
        catch (AnthropicRateLimitException ex)
        {
            throw new InvalidOperationException(
                "The Anthropic API rate limit was hit. Wait a minute, then re-run the summary from the saved transcript.", ex);
        }
        catch (AnthropicApiException ex)
        {
            throw new InvalidOperationException($"The Anthropic API returned an error: {ex.Message}", ex);
        }

        Log.LogInformation(
            "Anthropic request finished ({StopReason}): {Input} input tokens, {Output} output tokens",
            stopReason ?? "unknown", inputTokens, outputTokens);

        // What came back.
        if (stopReason == "refusal")
            throw new InvalidOperationException(
                "Claude declined this request. A summary can be redone from the saved transcript, also with a different model.");
        if (markdown.Length == 0)
            throw new InvalidOperationException("Claude returned an empty reply.");
        if (stopReason == "max_tokens")
        {
            Log.LogWarning("The reply hit the {Limit}-token output limit and is incomplete", MaxOutputTokens);
            markdown.AppendLine().AppendLine().Append("> *This summary was cut off at the output limit.*");
        }
        return markdown.ToString();
    }

    // Haiku has no server-side fallback, and older models predate the parameter.
    private static bool SupportsServerSideFallback(string model) =>
        model.StartsWith("claude-opus-5", StringComparison.OrdinalIgnoreCase)
        || model.StartsWith("claude-sonnet-5-5", StringComparison.OrdinalIgnoreCase)
        || model.StartsWith("claude-fable-5", StringComparison.OrdinalIgnoreCase);
}
