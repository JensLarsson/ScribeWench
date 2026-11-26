using ScribeWench.AI.Backends;
using ScribeWench.Core.Models;

namespace ScribeWench.CLI.Wizard.Environments;

/// <summary>
/// Strategy for everything setup does to the machine besides asking questions and calling web APIs:
/// looking at the hardware, running other programs, downloading files. The real one does it, a dry
/// run acts it out, and the tests make it up.
/// </summary>
public interface ISetupEnvironment
{
    /// <summary>Setup is only being tried out: nothing is downloaded, installed, started or saved.</summary>
    bool DryRun { get; }

    /// <summary>What a dry run has been told to pretend about the PC, if anything.</summary>
    string? Pretence();

    MachineInfo DetectMachine();

    /// <summary>The installed Claude Code's version, or null when it can't be run.</summary>
    Task<string?> ClaudeCodeVersionAsync(string executablePath, CancellationToken ct);

    Task<ClaudeAccount?> ClaudeCodeAccountAsync(string executablePath, CancellationToken ct);

    bool WhisperModelExists(WhisperConfig whisper);

    /// <summary>Reports the bytes received so far.</summary>
    Task DownloadWhisperModelAsync(WhisperConfig whisper, Action<long> onProgress, CancellationToken ct);

    /// <summary>Whether setup can install Ollama itself.</summary>
    Task<bool> CanInstallOllamaAsync(CancellationToken ct);

    /// <summary>Returns null when it worked, otherwise why not.</summary>
    Task<string?> InstallOllamaAsync(CancellationToken ct);

    /// <summary>Free space where Ollama keeps its models, in GB; null when it can't be told.</summary>
    double? FreeDiskGbForOllamaModels();

    bool FolderExists(string path);

    void CreateFolder(string path);

    bool FileExists(string path);

    /// <summary>Makes sure the file with the summary instructions is there, and returns where it is.</summary>
    string? EnsureInstructionsFile(string configuredPath);

    /// <summary>False when a page can't be opened (no desktop, for one).</summary>
    bool OpenInBrowser(string url);

    /// <summary>Reports bytes received and the total (0 when the server doesn't say).</summary>
    Task DownloadFileAsync(string url, string path, Action<long, long> onProgress, CancellationToken ct);
}
