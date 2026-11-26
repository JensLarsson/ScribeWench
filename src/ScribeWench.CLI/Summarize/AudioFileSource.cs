using NAudio.Wave;
using ScribeWench.AI.Notes;
using ScribeWench.AI.Summaries;
using ScribeWench.Bot.Audio;
using ScribeWench.CLI.Commands;
using ScribeWench.Core;
using ScribeWench.Core.Interfaces;
using ScribeWench.Core.Models;
using Spectre.Console;

namespace ScribeWench.CLI.Summarize;

// A recording: transcribed with Whisper, and the transcript saved beside the game's others.
public sealed class AudioFileSource(
    GameResolver games,
    ITranscriptionService transcription,
    INoteStore notes,
    OutputConfig output,
    RecordingConfig recording,
    TimeProvider time,
    IAnsiConsole console) : ISessionSource
{
    public bool Reads(string path) => true;

    public async Task<SourceResult> ReadAsync(string path, SummarizeCommandSettings settings)
    {
        // The game first: no point transcribing for an hour to then not know where it goes.
        var (game, ok) = await games.ResolveAsync(settings.Game, fromTranscript: null);
        if (!ok) return new SourceResult(null, ExitCode: 1);
        var campaign = game?.Name ?? output.CampaignName;

        // Whisper wants 16 kHz mono.
        var whisperWav = path;
        bool resample;
        using (var reader = new WaveFileReader(path))
            resample = reader.WaveFormat.SampleRate != 16000 || reader.WaveFormat.Channels != 1;
        if (resample)
            whisperWav = console.Status().Spinner(Spinner.Known.Dots).Start("[dim]Resampling to 16 kHz mono...[/]", _ =>
            {
                var tempDir = PathHelper.Expand(recording.TempDirectory);
                Directory.CreateDirectory(tempDir);
                return AudioResampler.ResampleToWhisperFormat(path, tempDir);
            });

        // Transcribe, then free the model's memory before anything else needs it.
        IReadOnlyList<TranscriptSegment> segments;
        try
        {
            if (game is not null)
                await transcription.SetVocabularyHintAsync(notes.CastHint(game.Folder));

            await console.Status().Spinner(Spinner.Known.Dots)
                .StartAsync("Loading Whisper model...", _ => transcription.EnsureModelReadyAsync());
            segments = await console.Status().Spinner(Spinner.Known.Dots)
                .StartAsync("Transcribing audio...", _ => transcription.TranscribeAsync(whisperWav, settings.SpeakerName, 0));
        }
        finally
        {
            await transcription.UnloadModelAsync();
            if (resample)
                try { File.Delete(whisperWav); } catch { }
        }

        if (segments.Count == 0)
        {
            console.MarkupLine("[yellow]No speech detected.[/] Try a longer clip or check the audio level.");
            return new SourceResult(null);
        }
        console.MarkupLine($"[green]Transcription complete:[/] {segments.Count} segments.");
        TranscriptTable.Write(console, segments);

        // Save the transcript.
        var startedAt = time.GetLocalNow().DateTime;
        var details = new SessionDetails(campaign, startedAt, game?.Folder, SessionDetails.BaseNameFor(campaign, startedAt));
        var folder = notes.TranscriptFolder(game?.Folder);
        Directory.CreateDirectory(folder);
        var transcriptPath = Path.Combine(folder, $"{details.FileBaseName}_transcript.md");
        await File.WriteAllTextAsync(transcriptPath, TranscriptDocument.Build(segments, campaign, startedAt, withEnds: false));
        console.MarkupLine($"[green]Transcript saved:[/] {Markup.Escape(transcriptPath)}");

        return new SourceResult(new LoadedSession(segments, details));
    }
}
