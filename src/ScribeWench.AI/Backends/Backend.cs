using Microsoft.Extensions.Logging;
using ScribeWench.AI.Summaries;
using ScribeWench.Core.Models;

namespace ScribeWench.AI.Backends;

// What the backends share: a model that takes a whole session gets it in one request, and one
// with a small context window reads a long session in parts.
public abstract class Backend(ILogger logger) : IBackend
{
    protected ILogger Log { get; } = logger;

    public abstract string Key { get; }
    public abstract string Label { get; }

    // For the log: "Ollama model gemma4:12b".
    protected abstract string Summarizing { get; }

    // The context window in tokens when a long session has to be read in parts. 0 = it takes one whole.
    protected virtual int Window => 0;

    // Room kept free in every request for a thinking model's thinking.
    protected virtual int ThinkingTokens => 0;

    public virtual Task CheckAsync(CancellationToken ct) => Task.CompletedTask;

    public abstract Task<string> CompleteAsync(string systemPrompt, string userMessage, CancellationToken ct = default);

    // One request with a limit on the reply. Only asked of backends that set a Window.
    protected virtual Task<ChunkedSummarizer.Reply> ChatAsync(string systemPrompt, string userMessage, int maxOutputTokens, CancellationToken ct) =>
        throw new NotSupportedException();

    public virtual async Task<SummaryResult> SummarizeAsync(
        IReadOnlyList<TranscriptSegment> transcript, SummarizationOptions options, CancellationToken ct = default)
    {
        if (Window > 0)
        {
            Log.LogInformation("Summarizing with {Backend} ({Context:N0}-token context window)", Summarizing, Window);
            // One per summary: it keeps a running estimate of how many characters make a token.
            var summarizer = new ChunkedSummarizer(Window, ChatAsync, Log, ThinkingTokens);
            return SummaryParser.Parse(await summarizer.SummarizeAsync(transcript, options, ct));
        }

        Log.LogInformation("Summarizing with {Backend}", Summarizing);
        var userMessage = Prompts.BuildUserMessage(Prompts.FormatTranscript(transcript), options);
        return SummaryParser.Parse(await CompleteAsync(Prompts.SystemPromptFor(options), userMessage, ct));
    }
}
