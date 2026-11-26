using Microsoft.Extensions.Logging;
using ScribeWench.Core.Interfaces;
using ScribeWench.Core.Models;
using Whisper.net;

namespace ScribeWench.Transcription;

public sealed class WhisperTranscriptionService(
    WhisperConfig config,
    WhisperModelFiles models,
    ILogger<WhisperTranscriptionService> logger) : ITranscriptionService, IAsyncDisposable
{
    // Held for loading, unloading and every transcription, so an unload can't slip in between a load and its use.
    private readonly SemaphoreSlim _lock = new(1, 1);
    private WhisperFactory? _factory;
    private WhisperProcessor? _processor;
    private bool _modelReady;
    private string? _vocabularyHint;

    public async Task EnsureModelReadyAsync(CancellationToken ct = default)
    {
        if (_modelReady) return;

        await _lock.WaitAsync(ct);
        try { await LoadLockedAsync(ct); }
        finally { _lock.Release(); }
    }

    public async Task UnloadModelAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            if (!_modelReady) return;

            if (_processor is not null) await _processor.DisposeAsync();
            _factory?.Dispose();
            _processor = null;
            _factory = null;
            _modelReady = false;
            logger.LogInformation("Whisper model unloaded.");
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task SetVocabularyHintAsync(string? hint, CancellationToken ct = default)
    {
        hint = string.IsNullOrWhiteSpace(hint) ? null : hint.Trim();

        await _lock.WaitAsync(ct);
        try
        {
            if (hint == _vocabularyHint) return;
            _vocabularyHint = hint;

            // The prompt is fixed when the processor is built; the loaded model itself can stay.
            if (_modelReady && _factory is not null)
            {
                if (_processor is not null) await _processor.DisposeAsync();
                _processor = BuildProcessor(_factory);
            }
            logger.LogInformation("Whisper vocabulary hint: {Hint}", hint ?? "(none)");
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<IReadOnlyList<TranscriptSegment>> TranscribeAsync(
        string wavFilePath, string speakerLabel, ulong userId, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            await LoadLockedAsync(ct);

            logger.LogInformation("Transcribing {File} for speaker {Speaker}", wavFilePath, speakerLabel);
            var segments = new List<TranscriptSegment>();
            await using var fileStream = File.OpenRead(wavFilePath);
            await foreach (var segment in _processor!.ProcessAsync(fileStream, ct))
            {
                if (string.IsNullOrWhiteSpace(segment.Text)) continue;
                segments.Add(new TranscriptSegment(speakerLabel, userId, segment.Start, segment.End, segment.Text.Trim()));
            }

            logger.LogInformation("Transcribed {Count} segments for {Speaker}", segments.Count, speakerLabel);
            return segments;
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task LoadLockedAsync(CancellationToken ct)
    {
        if (_modelReady) return;

        var modelPath = models.PathOf(config);
        await models.DownloadAsync(config, onProgress: null, ct);

        logger.LogInformation("Whisper: loading model from {Path} (GPU auto-detected if available)", modelPath);
        _factory = WhisperFactory.FromPath(modelPath);
        _processor = BuildProcessor(_factory);
        _modelReady = true;
        logger.LogInformation("Whisper model ready.");
    }

    private WhisperProcessor BuildProcessor(WhisperFactory factory)
    {
        var builder = factory.CreateBuilder()
            .WithLanguage(config.Language)
            // Discord strips silence from the audio, so continuous speech can arrive as one long run
            // with no pause to segment on. A new segment every N characters gives back-and-forth
            // conversation its own timestamp per line.
            .WithMaxSegmentLength(config.MaxSegmentLength)
            .SplitOnWord();

        // Whisper treats the prompt as text that came just before the audio, so names in it are
        // spelled the same way when they are heard.
        if (!string.IsNullOrWhiteSpace(_vocabularyHint))
            builder = builder.WithPrompt(_vocabularyHint);

        return builder.Build();
    }

    public async ValueTask DisposeAsync()
    {
        if (_processor is not null) await _processor.DisposeAsync();
        _factory?.Dispose();
        _lock.Dispose();
    }
}
