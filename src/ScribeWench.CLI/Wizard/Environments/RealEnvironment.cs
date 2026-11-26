using System.Diagnostics;
using ScribeWench.AI.Backends;
using ScribeWench.AI.Summaries;
using ScribeWench.Core.Models;
using ScribeWench.Transcription;

namespace ScribeWench.CLI.Wizard.Environments;

// The machine setup is running on, and everything setup really does to it.
public sealed class RealEnvironment(ClaudeCli claude, WhisperModelFiles whisperModels, IHttpClientFactory factory) : ISetupEnvironment
{
    public bool DryRun => false;

    public string? Pretence() => null;

    public MachineInfo DetectMachine() => MachineInfo.Detect();

    public Task<string?> ClaudeCodeVersionAsync(string executablePath, CancellationToken ct) => claude.GetVersionAsync(executablePath, ct);

    public Task<ClaudeAccount?> ClaudeCodeAccountAsync(string executablePath, CancellationToken ct) => claude.GetAccountAsync(executablePath, ct);

    public bool WhisperModelExists(WhisperConfig whisper) => whisperModels.Exists(whisper);

    public Task DownloadWhisperModelAsync(WhisperConfig whisper, Action<long> onProgress, CancellationToken ct) =>
        whisperModels.DownloadAsync(whisper, onProgress, ct);

    // On Windows, with winget, the package manager that comes with Windows 11 and current Windows 10.
    public async Task<bool> CanInstallOllamaAsync(CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            using var process = Process.Start(new ProcessStartInfo("winget", "--version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (process is null) return false;

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            await process.StandardOutput.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            return process.ExitCode == 0;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            return false;
        }
    }

    // winget downloads the installer from Ollama's own releases, checks it against the published
    // checksum and runs it for this user, without questions.
    public async Task<string?> InstallOllamaAsync(CancellationToken ct)
    {
        try
        {
            // Not redirected: winget draws its download progress on the console it is given.
            var start = new ProcessStartInfo("winget") { UseShellExecute = false };
            foreach (var argument in new[]
                     {
                         "install", "--id", "Ollama.Ollama", "--exact", "--source", "winget", "--silent",
                         "--accept-package-agreements", "--accept-source-agreements", "--disable-interactivity",
                     })
                start.ArgumentList.Add(argument);

            using var process = Process.Start(start);
            if (process is null) return "winget didn't start.";
            await process.WaitForExitAsync(ct);
            return process.ExitCode == 0 ? null : $"winget stopped with code 0x{process.ExitCode:X8}; what it said is above.";
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return ex.Message;
        }
    }

    public double? FreeDiskGbForOllamaModels()
    {
        try
        {
            var folder = Environment.GetEnvironmentVariable("OLLAMA_MODELS");
            if (string.IsNullOrWhiteSpace(folder))
                folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ollama", "models");
            var root = Path.GetPathRoot(Path.GetFullPath(folder));
            return string.IsNullOrEmpty(root) ? null : new DriveInfo(root).AvailableFreeSpace / (1024.0 * 1024 * 1024);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    public bool FolderExists(string path) => Directory.Exists(path);

    public void CreateFolder(string path) => Directory.CreateDirectory(path);

    public bool FileExists(string path) => File.Exists(path);

    public string? EnsureInstructionsFile(string configuredPath) => InstructionsStore.EnsureFile(configuredPath);

    public bool OpenInBrowser(string url)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or PlatformNotSupportedException)
        {
            return false;
        }
    }

    public async Task DownloadFileAsync(string url, string path, Action<long, long> onProgress, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var http = factory.CreateClient("downloads");
        http.Timeout = TimeSpan.FromHours(4);
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength ?? 0;
        // Under another name until it's whole, so a download that stops halfway isn't taken for the model.
        var partial = path + ".part";
        await using (var stream = await response.Content.ReadAsStreamAsync(ct))
        await using (var file = File.Create(partial))
        {
            var buffer = new byte[81920];
            long received = 0;
            int read;
            while ((read = await stream.ReadAsync(buffer, ct)) > 0)
            {
                await file.WriteAsync(buffer.AsMemory(0, read), ct);
                received += read;
                onProgress(received, total);
            }
        }
        File.Move(partial, path, overwrite: true);
    }
}
