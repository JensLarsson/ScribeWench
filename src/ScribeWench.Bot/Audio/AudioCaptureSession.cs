using Discord.Audio;
using Microsoft.Extensions.Logging;

namespace ScribeWench.Bot.Audio;

public sealed record AudioChunk(UserAudioBuffer Buffer, byte[] Pcm, DateTimeOffset StartedAt);

// The audio of one recording, a buffer per speaker. Discord hands out a stream per user; a
// background task per stream reads it into that user's buffer.
public sealed class AudioCaptureSession : IAsyncDisposable
{
    // 20 ms of 48 kHz 16-bit stereo, the size of a Discord voice frame.
    private const int FrameBytes = 3840;

    private readonly IAudioClient _audioClient;
    private readonly string _tempDirectory;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly Dictionary<ulong, string> _displayNames = [];
    private readonly Dictionary<ulong, Speaker> _speakers = [];
    private readonly object _speakersLock = new();
    private bool _stopped;

    private sealed record Speaker(UserAudioBuffer Buffer, Task Reader, CancellationTokenSource Cancel);

    public AudioCaptureSession(IAudioClient audioClient, string tempDirectory, TimeProvider time, ILogger logger)
    {
        _audioClient = audioClient;
        _tempDirectory = tempDirectory;
        _time = time;
        _logger = logger;
        _audioClient.StreamCreated += OnStreamCreatedAsync;
        _audioClient.StreamDestroyed += OnStreamDestroyedAsync;
    }

    // So a speaker shows by name instead of by user id.
    public void PreRegisterUser(ulong userId, string displayName) => _displayNames[userId] = displayName;

    private Task OnStreamCreatedAsync(ulong userId, AudioInStream stream)
    {
        if (_stopped) return Task.CompletedTask;

        lock (_speakersLock)
        {
            var cancel = new CancellationTokenSource();
            if (_speakers.TryGetValue(userId, out var existing))
            {
                // Discord closes and reopens the stream around silence. The same buffer goes on, so
                // nothing gathered so far is lost.
                existing.Cancel.Cancel();
                existing.Cancel.Dispose();
                _speakers[userId] = new Speaker(existing.Buffer, Task.Run(() => ReadAsync(stream, existing.Buffer, cancel.Token)), cancel);
            }
            else
            {
                var displayName = _displayNames.TryGetValue(userId, out var name) ? name : userId.ToString();
                _logger.LogInformation("New speaker in session: {Name}", displayName);
                var buffer = new UserAudioBuffer(userId, displayName, _tempDirectory, _time);
                _speakers[userId] = new Speaker(buffer, Task.Run(() => ReadAsync(stream, buffer, cancel.Token)), cancel);
            }
        }
        return Task.CompletedTask;
    }

    private Task OnStreamDestroyedAsync(ulong userId)
    {
        _logger.LogDebug("Audio stream destroyed for user {UserId}", userId);
        return Task.CompletedTask;
    }

    private async Task ReadAsync(AudioInStream stream, UserAudioBuffer buffer, CancellationToken ct)
    {
        var frame = new byte[FrameBytes];
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var read = await stream.ReadAsync(frame, 0, FrameBytes, ct);
                if (read == 0) break;
                await buffer.WriteAsync(frame, read, ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error reading audio stream for {Name}", buffer.DisplayName);
        }
    }

    private Speaker[] Speakers()
    {
        lock (_speakersLock) return [.. _speakers.Values];
    }

    // Everything gathered since the last call, without stopping the capture.
    public async Task<IReadOnlyList<AudioChunk>> TakeChunksAsync()
    {
        var chunks = new List<AudioChunk>();
        foreach (var speaker in Speakers())
            if (await speaker.Buffer.TakeChunkAsync() is { } chunk)
                chunks.Add(new AudioChunk(speaker.Buffer, chunk.Pcm, chunk.StartedAt));
        return chunks;
    }

    // Ends the capture and closes the WAV files. What was gathered last can still be taken afterwards.
    public async Task StopAsync()
    {
        _stopped = true;
        _audioClient.StreamCreated -= OnStreamCreatedAsync;
        _audioClient.StreamDestroyed -= OnStreamDestroyedAsync;

        foreach (var speaker in Speakers())
        {
            speaker.Cancel.Cancel();
            try { await speaker.Reader; } catch (OperationCanceledException) { }
            await speaker.Buffer.FlushAndCloseAsync();
            DownsampleRecording(speaker.Buffer.WavFilePath);
        }
    }

    // The kept recording goes from 48 kHz to Whisper's 16 kHz, in place. Transcription reads the
    // in-memory chunks, so this only changes what stays on disk for listening back.
    private void DownsampleRecording(string wavPath)
    {
        try
        {
            File.Move(AudioResampler.ResampleToWhisperFormat(wavPath, _tempDirectory), wavPath, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not downsample session recording {Path}", wavPath);
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var speaker in Speakers())
        {
            if (!_stopped)
            {
                speaker.Cancel.Cancel();
                try { await speaker.Reader; } catch (OperationCanceledException) { }
            }
            await speaker.Buffer.DisposeAsync();
            speaker.Cancel.Dispose();
        }
    }
}
