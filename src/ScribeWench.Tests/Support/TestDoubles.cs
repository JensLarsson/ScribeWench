using Microsoft.Extensions.Logging.Abstractions;
using ScribeWench.AI.Backends;
using ScribeWench.Core;
using ScribeWench.Core.Models;

namespace ScribeWench.Tests.Support;

/// <summary>A clock that stands still.</summary>
internal sealed class FixedTime(DateTime now) : TimeProvider
{
    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    public override DateTimeOffset GetUtcNow() => new(now, TimeSpan.Zero);
}

/// <summary>The Ollama program as a test has it: installed or not, and what starting it does.</summary>
internal sealed class FakeOllamaProgram(Func<bool>? start = null, bool installed = true) : IOllamaProgram
{
    public bool Installed => installed;
    public bool Start() => start?.Invoke() ?? false;
}

/// <summary>A backend that answers every summary with the same reply.</summary>
internal sealed class FakeBackend(string reply) : IBackend
{
    public string Key => "fake";
    public string Label => "a test";
    public Task CheckAsync(CancellationToken ct) => Task.CompletedTask;

    public Task<SummaryResult> SummarizeAsync(IReadOnlyList<TranscriptSegment> transcript, SummarizationOptions options, CancellationToken ct = default) =>
        Task.FromResult(SummaryParser.Parse(reply));

    public Task<string> CompleteAsync(string systemPrompt, string userMessage, CancellationToken ct = default) => Task.FromResult(reply);
}

/// <summary>The notes and summary pipeline, built the way the app builds it, over a test's own folders.</summary>
internal static class TestNotes
{
    /// <summary>Where note changes are backed up in tests that make none.</summary>
    public static string Backups => Path.Combine(Path.GetTempPath(), "scribewench-tests", "backups");

    /// <param name="home">Stands in for ~/.scribewench: games' own instructions and backups go here.</param>
    public static ObsidianNoteStore Store(AppConfig config, string home, TimeProvider? time = null) =>
        new(config.Obsidian, config.Output, new ScribePaths(home), time ?? TimeProvider.System, NullLogger<ObsidianNoteStore>.Instance);

    public static InstructionsStore Instructions(AppConfig config, string home, TimeProvider? time = null) =>
        new(config.Ai, new ScribePaths(home), time ?? TimeProvider.System, NullLogger<InstructionsStore>.Instance);

    public static SessionSummarizer Summarizer(AppConfig config, string home, string reply = "") =>
        new(new BackendRegistry([new FakeBackend(reply)], new AiConfig { Backend = "fake" }), Store(config, home), Instructions(config, home));
}
