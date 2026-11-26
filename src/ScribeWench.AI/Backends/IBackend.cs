using ScribeWench.Core.Models;

namespace ScribeWench.AI.Backends;

/// <summary>
/// Strategy for whoever writes the session notes: Claude Code, the Claude API, an OpenAI-compatible
/// service, Ollama or the built-in runner. Shared code gets one from <see cref="BackendRegistry"/>
/// and never branches on which it is.
/// </summary>
public interface IBackend
{
    /// <summary>The value of ai.backend in config.json that selects it.</summary>
    string Key { get; }

    /// <summary>What a note says it was generated with.</summary>
    string Label { get; }

    /// <summary>Says at startup what summaries will run on, so a wrong login shows before a session and not after.</summary>
    Task CheckAsync(CancellationToken ct);

    Task<SummaryResult> SummarizeAsync(IReadOnlyList<TranscriptSegment> transcript, SummarizationOptions options, CancellationToken ct = default);

    /// <summary>One plain request, for work other than summaries. Throws NotSupportedException where a game's notes can't fit.</summary>
    Task<string> CompleteAsync(string systemPrompt, string userMessage, CancellationToken ct = default);
}
