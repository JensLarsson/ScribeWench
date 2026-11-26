using ScribeWench.Core.Models;

namespace ScribeWench.Core.Interfaces;

public interface ITranscriptionService
{
    Task<IReadOnlyList<TranscriptSegment>> TranscribeAsync(string wavFilePath, string speakerLabel, ulong userId, CancellationToken ct = default);

    Task EnsureModelReadyAsync(CancellationToken ct = default);

    /// <summary>Names the coming audio is likely to contain, which steers how they are spelled. Null clears it.</summary>
    Task SetVocabularyHintAsync(string? hint, CancellationToken ct = default);

    /// <summary>Frees the model's memory. The next transcription loads it again.</summary>
    Task UnloadModelAsync(CancellationToken ct = default);
}
