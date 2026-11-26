using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace ScribeWench.Bot.Audio;

// To what Whisper reads: 16 kHz, 16-bit, mono.
public static class AudioResampler
{
    public static string ResampleToWhisperFormat(string inputWavPath, string outputDirectory)
    {
        var outputPath = Path.Combine(outputDirectory, Path.GetFileNameWithoutExtension(inputWavPath) + "_16k.wav");
        using var reader = new WaveFileReader(inputWavPath);
        Write(reader.ToSampleProvider(), reader.WaveFormat.Channels, outputPath);
        return outputPath;
    }

    // Straight from memory, without a temp file for the source.
    public static void ResampleToWhisperFormat(byte[] pcm, WaveFormat sourceFormat, string outputPath)
    {
        var raw = new RawSourceWaveStream(pcm, 0, pcm.Length, sourceFormat);
        Write(raw.ToSampleProvider(), sourceFormat.Channels, outputPath);
    }

    private static void Write(ISampleProvider source, int sourceChannels, string outputPath)
    {
        var mono = sourceChannels == 2 ? new StereoToMonoSampleProvider(source) : source;
        WaveFileWriter.CreateWaveFile16(outputPath, new WdlResamplingSampleProvider(mono, 16000));
    }
}
