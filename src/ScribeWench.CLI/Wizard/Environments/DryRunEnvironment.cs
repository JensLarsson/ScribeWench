using ScribeWench.AI.Backends;
using ScribeWench.Core;
using ScribeWench.Core.Interfaces;
using ScribeWench.Core.Models;
using Spectre.Console;

namespace ScribeWench.CLI.Wizard.Environments;

// What `setup --dry-run` was asked to pretend.
// Fresh: as on a PC with nothing set up: no saved settings, no Claude Code, no Ollama, no models.
// Step: how long each beat of an acted-out download takes.
public sealed record DryRunOptions(bool Fresh = false, double? RamGb = null, double? CardGb = null, TimeSpan? Step = null)
{
    public TimeSpan Pace => Step ?? TimeSpan.FromMilliseconds(120);
}

// Saved settings as `--dry-run --fresh` sees them: there are none, and none are written.
public sealed class NoSavedSettings(IConfigStore real) : IConfigStore
{
    public string ConfigPath => real.ConfigPath;
    public Task<AppConfig?> LoadAsync() => Task.FromResult<AppConfig?>(null);
    public Task SaveAsync(AppConfig config) => Task.CompletedTask;
}

// What has "happened" so far in a dry run: installed, started and downloaded in name only.
public sealed class DryRunState
{
    public bool OllamaInstalled { get; set; }
    public bool OllamaRunning { get; set; }
    // By the name Ollama would list them under.
    public HashSet<string> Models { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> WhisperModels { get; } = new(StringComparer.OrdinalIgnoreCase);
    // The model the last "does it run" check loaded.
    public string? Loaded { get; set; }

    public bool Has(string model) => Models.Contains(OllamaClient.Normalize(model));
}

// The machine as a dry run sees it. It looks at the real one (hardware, what is installed) unless
// told to pretend otherwise, and changes nothing: every download, install, start and new folder is
// acted out, so each screen of setup can be seen without anything landing on the disk.
public sealed class DryRunEnvironment : ISetupEnvironment, IOllamaProgram
{
    private readonly DryRunOptions _options;
    private readonly DryRunState _state;
    private readonly ISetupEnvironment _real;
    private readonly IAnsiConsole _console;
    private MachineInfo? _machine;

    public DryRunEnvironment(DryRunOptions options, DryRunState state, ISetupEnvironment real, IOllamaProgram realOllama, IAnsiConsole console)
    {
        _options = options;
        _state = state;
        _real = real;
        _console = console;
        state.OllamaInstalled = !options.Fresh && realOllama.Installed;
    }

    public bool DryRun => true;

    public string? Pretence()
    {
        var parts = new List<string>();
        if (_options.RamGb is { } ram) parts.Add($"{ram:0.#} GB of RAM");
        if (_options.CardGb is { } card) parts.Add(card > 0 ? $"a graphics card with {card:0.#} GB" : "no graphics card");
        if (_options.Fresh) parts.Add("nothing set up or installed yet");
        return parts.Count == 0 ? null : string.Join(", ", parts);
    }

    public MachineInfo DetectMachine()
    {
        if (_machine is not null) return _machine;

        var machine = _real.DetectMachine();
        if (_options.RamGb is { } ram)
            machine = machine with { RamGb = ram };
        if (_options.CardGb is { } card)
            machine = machine with { Cards = [new GraphicsCard(card > 0 ? "pretend graphics card" : "built-in graphics", card)] };
        return _machine = machine;
    }

    // ── Looking is real, unless the PC is to be taken for a bare one ─────────

    public Task<string?> ClaudeCodeVersionAsync(string executablePath, CancellationToken ct) =>
        _options.Fresh ? Task.FromResult<string?>(null) : _real.ClaudeCodeVersionAsync(executablePath, ct);

    public Task<ClaudeAccount?> ClaudeCodeAccountAsync(string executablePath, CancellationToken ct) =>
        _options.Fresh ? Task.FromResult<ClaudeAccount?>(null) : _real.ClaudeCodeAccountAsync(executablePath, ct);

    public bool WhisperModelExists(WhisperConfig whisper) =>
        _state.WhisperModels.Contains(whisper.ModelSize) || (!_options.Fresh && _real.WhisperModelExists(whisper));

    public bool Installed => _state.OllamaInstalled;

    public Task<bool> CanInstallOllamaAsync(CancellationToken ct) => _real.CanInstallOllamaAsync(ct);

    public double? FreeDiskGbForOllamaModels() => _real.FreeDiskGbForOllamaModels();

    public bool FolderExists(string path) => _real.FolderExists(path);

    public bool FileExists(string path) => !_options.Fresh && _real.FileExists(path);

    public bool OpenInBrowser(string url) => _real.OpenInBrowser(url);

    // ── Doing is acted out ───────────────────────────────────────────────────

    public async Task DownloadWhisperModelAsync(WhisperConfig whisper, Action<long> onProgress, CancellationToken ct)
    {
        var bytes = WhisperCatalog.Bytes(whisper.ModelSize);
        for (var beat = 1; beat <= 20; beat++)
        {
            await Task.Delay(_options.Pace, ct);
            onProgress(bytes * beat / 20);
        }
        _state.WhisperModels.Add(whisper.ModelSize);
    }

    public async Task<string?> InstallOllamaAsync(CancellationToken ct)
    {
        foreach (var line in new[]
                 {
                     "(dry run) here winget downloads Ollama, about 2 GB, showing how far it has come",
                     "(dry run) checks the download against the published checksum",
                     "(dry run) and installs it for this user",
                 })
        {
            _console.MarkupLine($"  [dim]{Markup.Escape(line)}[/]");
            await Task.Delay(_options.Pace * 5, ct);
        }
        _state.OllamaInstalled = true;
        return null;
    }

    public bool Start()
    {
        if (!_state.OllamaInstalled) return false;
        _state.OllamaRunning = true;
        return true;
    }

    public void CreateFolder(string path) { }

    // Where the instructions file would be; it isn't created.
    public string? EnsureInstructionsFile(string configuredPath) =>
        string.IsNullOrWhiteSpace(configuredPath) ? null : PathHelper.Expand(configuredPath.Trim());

    public async Task DownloadFileAsync(string url, string path, Action<long, long> onProgress, CancellationToken ct)
    {
        const long total = 5_000_000_000;
        for (var beat = 1; beat <= 20; beat++)
        {
            await Task.Delay(_options.Pace, ct);
            onProgress(total * beat / 20, total);
        }
    }
}
