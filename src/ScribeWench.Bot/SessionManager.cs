using Discord;
using Discord.Audio;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;
using ScribeWench.AI.Notes;
using ScribeWench.AI.Summaries;
using ScribeWench.Bot.Audio;
using ScribeWench.Core;
using ScribeWench.Core.Interfaces;
using ScribeWench.Core.Models;

namespace ScribeWench.Bot;

// One recording at a time, from join to posted summary: join, record, transcribe, summarize, post.
// The audio is transcribed every 30 seconds while the session runs, so the transcript file grows
// as people talk.
public sealed class SessionManager(
    ITranscriptionService transcription,
    SessionSummarizer summarizer,
    INoteStore notes,
    GameStore games,
    DiscordConfig discord,
    OutputConfig output,
    RecordingConfig recording,
    TimeProvider time,
    ILogger<SessionManager> logger) : IAsyncDisposable
{
    private static readonly TimeSpan ChunkInterval = TimeSpan.FromSeconds(30);

    // The game being recorded or summarized; null when idle.
    private Game? _game;
    private IAudioClient? _audioClient;
    private SocketVoiceChannel? _voiceChannel;
    private AudioCaptureSession? _capture;
    private RecordingSession? _session;
    private string? _voiceChannelName;

    private readonly List<TranscriptSegment> _segments = [];
    private string? _transcriptPath;
    private CancellationTokenSource? _chunkCancel;
    private Task? _chunkLoop;
    private Task? _processing;
    // Each batch waits for the one before, which keeps the 30-second timer on schedule when Whisper
    // is slower than real time.
    private Task _transcriptions = Task.CompletedTask;
    // 1 while a join is connecting. Without it, repeated joins all reach ConnectAsync at once.
    private int _joining;

    public bool IsRecording => _session?.IsActive ?? false;
    public bool IsProcessing => _processing is { IsCompleted: false };
    // `stop` was used and the session is winding down.
    public bool IsFinishing => _session is { IsActive: false };
    public Game? CurrentGame => _game;

    private string TempDirectory => PathHelper.Expand(recording.TempDirectory);

    public async Task<bool> JoinAndStartAsync(SocketGuildUser caller, ISocketMessageChannel textChannel, Game game, CancellationToken ct = default)
    {
        if (Interlocked.Exchange(ref _joining, 1) == 1)
        {
            await textChannel.SendMessageAsync("Already joining, give it a moment.");
            return false;
        }

        try
        {
            return await JoinAsync(caller, textChannel, game, ct);
        }
        finally
        {
            Volatile.Write(ref _joining, 0);
        }
    }

    private async Task<bool> JoinAsync(SocketGuildUser caller, ISocketMessageChannel textChannel, Game game, CancellationToken ct)
    {
        // Whether a recording can start now.
        if (IsRecording)
        {
            await textChannel.SendMessageAsync($"Already recording **{Format.Sanitize(_game?.Name ?? "a game")}**.");
            return false;
        }
        // The whole wind-down after `stop`, including the moments before the summary task starts.
        if (IsFinishing || IsProcessing)
        {
            await textChannel.SendMessageAsync("Still finishing the last session. Try again in a minute.");
            return false;
        }
        var voiceChannel = caller.VoiceChannel;
        if (voiceChannel is null)
        {
            await textChannel.SendMessageAsync("Join a voice channel first.");
            return false;
        }

        _voiceChannelName = voiceChannel.Name;
        _voiceChannel = voiceChannel;
        _game = game;

        try
        {
            // Whisper, with the cast's names to spell by. Set before loading, so the model is built with them.
            await transcription.SetVocabularyHintAsync(notes.CastHint(game.Folder), ct);
            await transcription.EnsureModelReadyAsync(ct);

            // The voice channel.
            logger.LogInformation("Connecting to voice channel {Channel}...", voiceChannel.Name);
            try
            {
                _audioClient = await voiceChannel.ConnectAsync();
            }
            catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
            {
                // Discord.Net reports a voice handshake that never completes as a bare "A task was canceled".
                throw new TimeoutException("the voice connection to Discord timed out.", ex);
            }
            logger.LogInformation("Voice connection established.");

            // The capture, one per recording. People already in the channel go by their display names.
            _capture = new AudioCaptureSession(_audioClient, TempDirectory, time, logger);
            foreach (var member in voiceChannel.ConnectedUsers.Where(u => !u.IsBot))
                _capture.PreRegisterUser(member.Id, member.DisplayName ?? member.Username);

            _session = new RecordingSession { GuildId = voiceChannel.Guild?.Id ?? 0, StartedAt = time.GetUtcNow() };

            // The transcript file, decided now so the first batch has somewhere to go.
            if (output.SaveMarkdown)
            {
                var folder = notes.TranscriptFolder(game.Folder);
                Directory.CreateDirectory(folder);
                _transcriptPath = Path.Combine(folder, $"{BaseNameFor(game, _session)}_transcript.md");
            }

            // Transcribing as it goes.
            _segments.Clear();
            _chunkCancel = new CancellationTokenSource();
            var stop = _chunkCancel.Token;
            _chunkLoop = Task.Run(() => TranscribePeriodicallyAsync(stop));

            await textChannel.SendMessageAsync(
                $"🔴 Recording **{Format.Sanitize(game.Name)}**. `{discord.CommandPrefix} stop` to end.");
            logger.LogInformation("Recording {Game} started in guild {GuildId}, channel {ChannelId}",
                game.Name, voiceChannel.Guild?.Id, voiceChannel.Id);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to start recording");
            _game = null;
            _session = null;
            // A half-made connection leaves the bot "in" the channel, and the next join then times out.
            try { await voiceChannel.DisconnectAsync(); }
            catch (Exception dex) { logger.LogWarning(dex, "Could not leave the voice channel"); }
            _voiceChannel = null;
            _audioClient = null;
            // The model may have loaded before the voice connection failed.
            await UnloadWhisperAsync();
            // Said in the reply: it is usually something the admin has to fix (permissions, the notes folder).
            await textChannel.SendMessageAsync($"Couldn't start recording: {Format.Sanitize(ex.Message)}");
            return false;
        }
    }

    public async Task StopAndProcessAsync(ISocketMessageChannel textChannel, CancellationToken ct = default)
    {
        if (!IsRecording || _capture is null || _session is null)
            return;

        _session.StoppedAt = time.GetUtcNow();

        // Stop the 30-second loop, and let what it started finish.
        if (_chunkCancel is not null)
        {
            _chunkCancel.Cancel();
            _chunkCancel.Dispose();
            _chunkCancel = null;
        }
        if (_chunkLoop is not null)
        {
            try { await _chunkLoop; } catch (OperationCanceledException) { }
            _chunkLoop = null;
        }
        try { await _transcriptions; }
        catch (Exception ex) { logger.LogWarning(ex, "Periodic transcription had errors"); }

        // Stop the capture, and take what arrived since the last batch.
        await _capture.StopAsync();
        var lastChunks = await _capture.TakeChunksAsync();

        // Leave the channel. Stopping the audio client isn't enough: the gateway would keep the bot
        // "in" the channel, and the next join would time out.
        if (_voiceChannel is not null)
        {
            await _voiceChannel.DisconnectAsync();
            _voiceChannel = null;
        }
        _audioClient = null;

        await textChannel.SendMessageAsync("Stopped. Writing the summary…");
        _processing = Task.Run(() => ProcessAsync(lastChunks, textChannel, ct), ct);
    }

    private async Task TranscribePeriodicallyAsync(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(ChunkInterval, time, stop);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            if (stop.IsCancellationRequested) return;
            if (_capture is null) continue;

            // Taking the audio is quick and stays on schedule; transcribing it queues up behind the batch before.
            var chunks = await _capture.TakeChunksAsync();
            if (chunks.Count > 0)
                _transcriptions = _transcriptions
                    .ContinueWith(_ => TranscribeAsync(chunks, CancellationToken.None), TaskScheduler.Default)
                    .Unwrap();
        }
    }

    private async Task ProcessAsync(IReadOnlyList<AudioChunk> lastChunks, ISocketMessageChannel textChannel, CancellationToken ct)
    {
        try
        {
            // The rest of the audio. After it Whisper's memory is freed, rather than held through the
            // summary and the idle time after.
            if (lastChunks.Count > 0)
                await TranscribeAsync(lastChunks, ct);
            await UnloadWhisperAsync();

            if (_segments.Count == 0)
            {
                await textChannel.SendMessageAsync("No speech was recorded, so there's no summary.");
                return;
            }

            // The transcript is saved before anything is asked of the model.
            if (output.SaveMarkdown && _transcriptPath is not null)
                await SaveTranscriptAsync(ct);

            var game = _game!;
            var session = _session!;

            // The summary. Its context is gathered now, not at the start, so notes added mid-session are in it.
            var details = new SessionDetails(game.Name, session.StartedAt.LocalDateTime, game.Folder, BaseNameFor(game, session));
            SessionDraft draft;
            try
            {
                draft = await summarizer.WriteAsync(_segments, summarizer.Prepare(details), ct);
            }
            catch
            {
                // A summary that failed can't say what the recording was, so it counts as a session.
                await CountSessionAsync(game, session);
                throw;
            }
            // Talking about the campaign isn't a session of the game.
            if (!draft.Summary.NotASession) await CountSessionAsync(game, session);
            var saved = await summarizer.SaveAsync(draft, details, ct: ct);

            // Tell the channel.
            if (output.PostToDiscord)
            {
                foreach (var chunk in DiscordText.SplitIntoChunks(summarizer.ForDiscord(draft, saved)))
                {
                    await textChannel.SendMessageAsync(chunk);
                    await Task.Delay(500, ct);
                }
            }
            else if (draft.Summary.NotASession)
                await textChannel.SendMessageAsync("That didn't sound like a game session, so there's no summary.");
            else if (saved.NotePath is not null)
                await textChannel.SendMessageAsync($"Summary saved: {DiscordText.Code(Path.GetFileName(saved.NotePath))}");
            else
                // Only when the admin turned off posting, saving and the notes folder.
                await textChannel.SendMessageAsync("Summary written, but posting and saving are both turned off in setup.");

            // Changes the model asked for that don't fit the notes as they are.
            if (saved.Notes.NotChanged.Count > 0)
            {
                var problems = string.Join('\n', saved.Notes.NotChanged.Select(p => "- " + Format.Sanitize(p)));
                await textChannel.SendMessageAsync(
                    DiscordText.SplitIntoChunks("Some changes to the notes couldn't be made:\n" + problems)[0],
                    allowedMentions: AllowedMentions.None);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error during session processing");
            var transcriptNote = _transcriptPath is not null && File.Exists(_transcriptPath)
                ? " The transcript is saved, so the summary can be redone with `scribewench summarize`."
                : "";
            await textChannel.SendMessageAsync($"Couldn't write the summary: {Format.Sanitize(ex.Message)}{transcriptNote}");
        }
        finally
        {
            // For the paths that failed before unloading above.
            await UnloadWhisperAsync();
            _session = null;
            _game = null;
            _capture = null;
            _segments.Clear();
            _transcriptPath = null;
            _transcriptions = Task.CompletedTask;
        }
    }

    private async Task TranscribeAsync(IReadOnlyList<AudioChunk> chunks, CancellationToken ct)
    {
        var added = new List<TranscriptSegment>();
        foreach (var (buffer, pcm, startedAt) in chunks)
        {
            if (pcm.Length == 0) continue;

            var offset = startedAt - _session!.StartedAt;
            logger.LogInformation("Transcribing {Duration:F1}s chunk for {Name} (session offset {Offset})",
                pcm.Length / (48000.0 * 2), buffer.DisplayName, offset);

            var wavPath = Path.Combine(TempDirectory, $"{buffer.UserId}_chunk_{time.GetUtcNow():yyyyMMddHHmmssfff}_16k.wav");
            try
            {
                Directory.CreateDirectory(TempDirectory);
                AudioResampler.ResampleToWhisperFormat(pcm, UserAudioBuffer.MonoFormat, wavPath);
                var segments = await transcription.TranscribeAsync(wavPath, buffer.DisplayName, buffer.UserId, ct);

                // Whisper's times count from the start of the chunk.
                foreach (var segment in segments)
                    added.Add(segment with { Start = segment.Start + offset, End = segment.End + offset });

                // The speaker's session recording has this audio too.
                try { File.Delete(wavPath); }
                catch (Exception ex) { logger.LogWarning(ex, "Could not delete chunk file {Path}", wavPath); }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to transcribe chunk for {Name}", buffer.DisplayName);
            }
        }
        if (added.Count == 0) return;

        _segments.AddRange(added);
        _segments.Sort((a, b) => a.Start.CompareTo(b.Start));

        if (output.SaveMarkdown && _transcriptPath is not null && _session is not null)
            await SaveTranscriptAsync(ct);
    }

    private async Task SaveTranscriptAsync(CancellationToken ct)
    {
        if (_transcriptPath is null || _session is null) return;

        var document = TranscriptDocument.Build(
            _segments, _game?.Name ?? output.CampaignName, _session.StartedAt.LocalDateTime, withEnds: true);
        await File.WriteAllTextAsync(_transcriptPath, document, ct);
        logger.LogInformation("Transcript updated: {Count} segments → {Path}", _segments.Count, _transcriptPath);
    }

    private async Task CountSessionAsync(Game game, RecordingSession session)
    {
        try { await games.RecordSessionAsync(game.GuildId, game.Name, session.StartedAt); }
        catch (Exception ex) { logger.LogWarning(ex, "Could not update the session count for {Game}", game.Name); }
    }

    private async Task UnloadWhisperAsync()
    {
        try { await transcription.UnloadModelAsync(); }
        catch (Exception ex) { logger.LogWarning(ex, "Could not unload the Whisper model"); }
    }

    public string GetStatus()
    {
        var game = $"**{Format.Sanitize(_game?.Name ?? "a game")}**";
        if (IsFinishing || IsProcessing) return $"Writing the summary for {game}…";
        if (_session is null) return "Not recording.";

        var duration = time.GetUtcNow() - _session.StartedAt;
        return $"🔴 {game}, {duration:h\\:mm\\:ss} in {Format.Sanitize(_voiceChannelName ?? "voice")}.";
    }

    private static string BaseNameFor(Game game, RecordingSession session) =>
        SessionDetails.BaseNameFor(game.Name, session.StartedAt.LocalDateTime);

    public async ValueTask DisposeAsync()
    {
        if (_chunkCancel is not null)
        {
            _chunkCancel.Cancel();
            _chunkCancel.Dispose();
        }
        if (_capture is not null)
            await _capture.DisposeAsync();
        if (_voiceChannel is not null)
            try { await _voiceChannel.DisconnectAsync(); } catch { }
        _audioClient?.Dispose();
    }
}
