using Microsoft.Extensions.Logging;
using ScribeWench.Core;
using ScribeWench.Core.Models;
using Whisper.net.Ggml;

namespace ScribeWench.Transcription;

// The Whisper model files on disk: where one is, and fetching it when it isn't there.
public sealed class WhisperModelFiles(ILogger<WhisperModelFiles> logger)
{
    public string PathOf(WhisperConfig config) =>
        Path.Combine(PathHelper.Expand(config.ModelDirectory), $"ggml-{config.ModelSize}.bin");

    public bool Exists(WhisperConfig config) => File.Exists(PathOf(config));

    // Does nothing when the model is there. Reports the bytes received so far; the downloader
    // doesn't say how many there will be.
    public async Task DownloadAsync(WhisperConfig config, Action<long>? onProgress = null, CancellationToken ct = default)
    {
        var modelPath = PathOf(config);
        if (File.Exists(modelPath)) return;

        Directory.CreateDirectory(Path.GetDirectoryName(modelPath)!);
        logger.LogInformation("Downloading Whisper model {ModelSize} to {Path}...", config.ModelSize, modelPath);

        // Under another name until it is whole: a download cut short must not be taken for the model.
        var partial = modelPath + ".part";
        using (var modelStream = await WhisperGgmlDownloader.Default.GetGgmlModelAsync(TypeOf(config.ModelSize), QuantizationType.NoQuantization, ct))
        await using (var fileStream = File.Create(partial))
        {
            var buffer = new byte[1 << 20];
            long received = 0;
            int read;
            while ((read = await modelStream.ReadAsync(buffer, ct)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, read), ct);
                received += read;
                onProgress?.Invoke(received);
            }
        }
        File.Move(partial, modelPath, overwrite: true);
    }

    private static GgmlType TypeOf(string modelSize) => modelSize.ToLowerInvariant() switch
    {
        "tiny" => GgmlType.Tiny,
        "base" => GgmlType.Base,
        "small" => GgmlType.Small,
        "medium" => GgmlType.Medium,
        "large" => GgmlType.LargeV3,
        _ => GgmlType.Base,
    };
}
