using NAudio.Wave;

namespace ScribeWench.Bot.Audio;

// One speaker's audio: written to a WAV file for the whole session, and kept in memory since the
// last chunk was taken, so the session can be transcribed while it is still going.
public sealed class UserAudioBuffer : IAsyncDisposable
{
    // One microphone has no stereo content (Discord sends it on both channels), so it is mixed
    // down before it is stored. Whisper would do the same.
    public static readonly WaveFormat MonoFormat = new(48000, 16, 1);

    private readonly TimeProvider _time;
    private readonly WaveFileWriter _writer;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly MemoryStream _chunk = new();
    private DateTimeOffset _chunkStartedAt;
    private bool _closed;

    public ulong UserId { get; }
    public string DisplayName { get; }
    public string WavFilePath { get; }

    public UserAudioBuffer(ulong userId, string displayName, string tempDirectory, TimeProvider time)
    {
        UserId = userId;
        DisplayName = displayName;
        _time = time;
        _chunkStartedAt = time.GetUtcNow();
        Directory.CreateDirectory(tempDirectory);
        WavFilePath = Path.Combine(tempDirectory, $"{userId}_{time.GetUtcNow():yyyyMMddHHmmss}.wav");
        _writer = new WaveFileWriter(WavFilePath, MonoFormat);
    }

    // Takes Discord's 48 kHz 16-bit stereo.
    public async Task WriteAsync(byte[] buffer, int count, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            if (_closed) return;

            var mono = DownmixToMono(buffer, count);
            if (_chunk.Length == 0) _chunkStartedAt = _time.GetUtcNow();
            _writer.Write(mono, 0, mono.Length);
            _chunk.Write(mono, 0, mono.Length);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private static byte[] DownmixToMono(byte[] stereo, int count)
    {
        // Four bytes to a stereo frame.
        var frames = count / 4;
        var mono = new byte[frames * 2];
        for (var i = 0; i < frames; i++)
        {
            short left = (short)(stereo[i * 4] | (stereo[i * 4 + 1] << 8));
            short right = (short)(stereo[i * 4 + 2] | (stereo[i * 4 + 3] << 8));
            var average = (short)((left + right) / 2);
            mono[i * 2] = (byte)(average & 0xFF);
            mono[i * 2 + 1] = (byte)((average >> 8) & 0xFF);
        }
        return mono;
    }

    // The audio since the last call and when it began arriving, or null when there is none.
    public async Task<(byte[] Pcm, DateTimeOffset StartedAt)?> TakeChunkAsync()
    {
        await _writeLock.WaitAsync();
        try
        {
            if (_chunk.Length == 0) return null;

            var pcm = _chunk.ToArray();
            _chunk.SetLength(0);
            _chunk.Position = 0;
            return (pcm, _chunkStartedAt);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task FlushAndCloseAsync()
    {
        await _writeLock.WaitAsync();
        try
        {
            if (_closed) return;
            _closed = true;
            _writer.Dispose();
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_closed) return;
        await FlushAndCloseAsync();
        _writeLock.Dispose();
        _chunk.Dispose();
    }
}
