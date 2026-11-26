using System.Net;
using Microsoft.Extensions.DependencyInjection;
using ScribeWench.AI.Backends;
using ScribeWench.CLI;
using ScribeWench.CLI.Wizard;
using ScribeWench.CLI.Wizard.Environments;
using ScribeWench.Core.Interfaces;
using ScribeWench.Core.Models;
using Spectre.Console;
using Spectre.Console.Rendering;
using Spectre.Console.Testing;

namespace ScribeWench.Tests.Support;

/// <summary>
/// Runs the setup wizard against a made-up machine and a made-up internet, with the keys a user
/// would press queued up beforehand. Nothing outside the test is touched.
/// </summary>
internal sealed class WizardRun
{
    public const string Token = "not-a-real-bot-token";
    public const string BotId = "123456789012345678";
    public const string Ollama = "http://localhost:11434";

    public FakeWeb Web { get; } = new();
    public FakeMachine Machine { get; } = new();
    public MemoryConfigStore Store { get; } = new();
    public TestConsole Console { get; } = new TestConsole().Width(160).Interactive();
    public FakeTerminal Terminal { get; }

    public WizardRun() => Terminal = new FakeTerminal(Console);

    public AppConfig Saved => Store.Saved ?? throw new InvalidOperationException("Nothing was saved. The screen ended with:\n" + Tail());
    public string Screen => Console.Output;

    // ─── What the user presses ───────────────────────────────────────────────

    public WizardRun Enter(int times = 1)
    {
        for (var i = 0; i < times; i++) Console.Input.PushKey(ConsoleKey.Enter);
        return this;
    }

    /// <summary>Moves down a list and picks.</summary>
    public WizardRun Pick(int down)
    {
        for (var i = 0; i < down; i++) Console.Input.PushKey(ConsoleKey.DownArrow);
        return Enter();
    }

    public WizardRun Type(string text)
    {
        Console.Input.PushTextWithEnter(text);
        return this;
    }

    public WizardRun Yes() => Type("y");
    public WizardRun No() => Type("n");

    // ─── What is out there ───────────────────────────────────────────────────

    /// <summary>A Discord that knows the test bot: its token works, it may read messages, and it is in one server.</summary>
    public WizardRun WithDiscord(bool messageContent = true, params string[] servers)
    {
        servers = servers.Length > 0 ? servers : ["The Tavern"];
        Web.On("GET", "https://discord.com/api/v10/", request => request.Authorization == $"Bot {Token}"
            ? request.Url switch
            {
                var url when url.EndsWith("/users/@me") => FakeWeb.Json(new { id = BotId, username = "ScribeWench" }),
                var url when url.EndsWith("/applications/@me") => FakeWeb.Json(new { id = BotId, name = "ScribeWench", flags = messageContent ? 1 << 19 : 0 }),
                var url when url.EndsWith("/users/@me/guilds") => FakeWeb.Json(servers.Select((name, i) => new { id = i.ToString(), name })),
                _ => FakeWeb.Json(new { message = "404: Not Found" }, HttpStatusCode.NotFound),
            }
            : FakeWeb.Json(new { message = "401: Unauthorized", code = 0 }, HttpStatusCode.Unauthorized));
        return this;
    }

    /// <summary>An Ollama on this machine holding the given models; any other can be downloaded unless its name is in <paramref name="unknown"/>.</summary>
    public WizardRun WithOllama(string[]? downloaded = null, string[]? unknown = null, string? loadError = null, string[]? tooLarge = null)
    {
        const string noMemory = "model requires more system memory (41.2 GiB) than is available (27.9 GiB)";
        var models = new List<string>(downloaded ?? []);
        Web.On("GET", $"{Ollama}/api/version", new { version = "0.40.18" })
            .On("GET", $"{Ollama}/api/tags", _ => FakeWeb.Json(new
            {
                models = models.Select(name => new { name, size = 8_000_000_000, details = new { context_length = 262144 }, capabilities = new[] { "completion", "thinking" } }),
            }))
            .On("POST", $"{Ollama}/api/show", request => models.Contains(request.Json.GetProperty("model").GetString()!)
                ? FakeWeb.Json(new { details = new { parameter_size = "27B" }, model_info = new Dictionary<string, object> { ["x.context_length"] = 262144 }, capabilities = new[] { "completion", "thinking" } })
                : FakeWeb.Json(new { error = "model not found" }, HttpStatusCode.NotFound))
            .On("POST", $"{Ollama}/api/pull", request =>
            {
                var name = request.Json.GetProperty("model").GetString()!;
                if (unknown?.Contains(name) == true)
                    return FakeWeb.Lines(new { status = "pulling manifest" }, new { error = "pull model manifest: file does not exist" });
                models.Add(name);
                return FakeWeb.Lines(
                    new { status = "pulling manifest" },
                    new { status = "pulling aaa", digest = "sha256:aaa", total = 17_000_000_000, completed = 17_000_000_000 },
                    new { status = "success" });
            })
            .On("POST", $"{Ollama}/api/chat", request =>
            {
                var error = tooLarge?.Contains(request.Json.GetProperty("model").GetString()) == true ? noMemory : loadError;
                return error is null
                    ? FakeWeb.Json(new { message = new { role = "assistant", content = "ready" }, done = true })
                    : FakeWeb.Json(new { error }, HttpStatusCode.InternalServerError);
            })
            .On("GET", $"{Ollama}/api/ps", _ => FakeWeb.Json(new { models = models.Select(name => new { name, size = 22_000_000_000, size_vram = 13_000_000_000 }) }))
            .On("POST", $"{Ollama}/api/generate", new { done = true });
        return this;
    }

    /// <summary>
    /// Set to run the wizard the way `setup --dry-run` does. The made-up machine and web then stand
    /// for the real ones the dry run looks at, and must come through it untouched.
    /// </summary>
    public DryRunOptions? DryRun { get; init; }

    /// <summary>The wizard as setup composes it, with the test's machine, web, console and settings in place of the real ones.</summary>
    public ServiceProvider Services()
    {
        var services = new ServiceCollection().AddSetupWizard();
        services.AddSingleton<IAnsiConsole>(Console);
        services.AddSingleton<ITerminal>(Terminal);
        services.AddSingleton<IConfigStore>(Store);
        services.AddSingleton<IHttpClientFactory>(Web);
        services.AddSingleton<ISetupEnvironment>(Machine);
        services.AddSingleton<IOllamaProgram>(Machine);

        if (DryRun is not null)
        {
            var options = DryRun with { Step = TimeSpan.Zero };
            var state = new DryRunState();
            var dryRun = new DryRunEnvironment(options, state, Machine, Machine, Console);
            services.AddSingleton<ISetupEnvironment>(dryRun);
            services.AddSingleton<IOllamaProgram>(dryRun);
            services.AddSingleton<IHttpClientFactory>(new DryRunWeb(Web, new DryRunOllama(state, options, dryRun.DetectMachine, Web)));
            if (options.Fresh) services.AddSingleton<IConfigStore>(new NoSavedSettings(Store));
        }
        return services.BuildServiceProvider();
    }

    public async Task<int> RunAsync()
    {
        await using var services = Services();
        try
        {
            return await services.GetRequiredService<SetupWizard>().RunAsync();
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("No input available"))
        {
            throw new InvalidOperationException("The wizard asked for more than the test answered. The screen ended with:\n" + Tail(), ex);
        }
        finally
        {
            // For reading the wizard the way a user sees it: set SCRIBEWENCH_WIZARD_SCREENS to a folder.
            if (Environment.GetEnvironmentVariable("SCRIBEWENCH_WIZARD_SCREENS") is { Length: > 0 } folder)
            {
                Directory.CreateDirectory(folder);
                File.WriteAllText(Path.Combine(folder, $"{Name}.txt"), Screen);
            }
        }
    }

    /// <summary>Names the saved screen after the test.</summary>
    public string Name { get; init; } = "wizard";

    private string Tail() => string.Join('\n', Screen.Split('\n').TakeLast(25));
}

/// <summary>A machine as the wizard sees it, set by the test.</summary>
internal sealed class FakeMachine : ISetupEnvironment, IOllamaProgram
{
    public double RamGb { get; set; } = 31.1;
    public List<GraphicsCard> Cards { get; set; } = [new("AMD Radeon RX 9070 XT", 15.8), new("AMD Radeon(TM) Graphics", 0.5)];
    public string? ClaudeCode { get; set; }
    public ClaudeAccount? ClaudeAccount { get; set; } = new(true, "claude.ai", "dm@example.com", "max");
    public bool OllamaIsInstalled { get; set; } = true;
    /// <summary>Whether Windows' package manager is there to install Ollama with.</summary>
    public bool Winget { get; set; }
    /// <summary>Called when the wizard installs Ollama; returns null when it worked, otherwise why not.</summary>
    public Func<string?> OnInstallOllama { get; set; } = () => "winget stopped with code 0x8A150014; what it said is above.";
    public int OllamaInstalls { get; private set; }
    /// <summary>Called when the wizard starts Ollama; returns whether it started.</summary>
    public Func<bool> OnStartOllama { get; set; } = () => false;
    /// <summary>Called when the wizard opens a page in the browser.</summary>
    public Action<string> OnOpen { get; set; } = _ => { };
    public double? FreeDiskGb { get; set; } = 200;
    public HashSet<string> WhisperModelsOnDisk { get; } = [];
    public HashSet<string> MissingFolders { get; } = [];

    public List<string> Opened { get; } = [];
    public List<string> WhisperDownloads { get; } = [];
    public List<string> CreatedFolders { get; } = [];

    public bool DryRun => false;
    public string? Pretence() => null;

    public MachineInfo DetectMachine() => new(RamGb, Cards);

    public Task<string?> ClaudeCodeVersionAsync(string executablePath, CancellationToken ct) => Task.FromResult(ClaudeCode);

    public Task<ClaudeAccount?> ClaudeCodeAccountAsync(string executablePath, CancellationToken ct) => Task.FromResult(ClaudeAccount);

    public bool WhisperModelExists(WhisperConfig whisper) => WhisperModelsOnDisk.Contains(whisper.ModelSize);

    public Task DownloadWhisperModelAsync(WhisperConfig whisper, Action<long> onProgress, CancellationToken ct)
    {
        onProgress(1_000_000);
        WhisperDownloads.Add(whisper.ModelSize);
        WhisperModelsOnDisk.Add(whisper.ModelSize);
        return Task.CompletedTask;
    }

    public bool Installed => OllamaIsInstalled;
    public bool Start() => OnStartOllama();
    public Task<bool> CanInstallOllamaAsync(CancellationToken ct) => Task.FromResult(Winget);

    public Task<string?> InstallOllamaAsync(CancellationToken ct)
    {
        OllamaInstalls++;
        return Task.FromResult(OnInstallOllama());
    }

    public double? FreeDiskGbForOllamaModels() => FreeDiskGb;

    public bool FolderExists(string path) => !MissingFolders.Contains(path);

    public void CreateFolder(string path)
    {
        CreatedFolders.Add(path);
        MissingFolders.Remove(path);
    }

    public bool FileExists(string path) => false;
    public string? EnsureInstructionsFile(string configuredPath) => null;

    public bool OpenInBrowser(string url)
    {
        Opened.Add(url);
        OnOpen(url);
        return true;
    }

    public Task DownloadFileAsync(string url, string path, Action<long, long> onProgress, CancellationToken ct)
    {
        onProgress(1000, 1000);
        return Task.CompletedTask;
    }
}

/// <summary>A terminal that draws braille unless the test says otherwise, showing it on the test's console.</summary>
internal sealed class FakeTerminal(IAnsiConsole console) : ITerminal
{
    public bool DrawsBraille { get; set; } = true;
    public void Write(IRenderable picture) => console.Write(picture);
}

internal sealed class MemoryConfigStore : IConfigStore
{
    public AppConfig? Existing { get; set; }
    public AppConfig? Saved { get; private set; }
    public string ConfigPath => @"C:\Users\dm\.scribewench\config.json";

    public Task<AppConfig?> LoadAsync() => Task.FromResult(Existing);

    public Task SaveAsync(AppConfig config)
    {
        Saved = config;
        return Task.CompletedTask;
    }
}
