using System.Net;
using Microsoft.Extensions.DependencyInjection;
using ScribeWench.CLI.Wizard;
using ScribeWench.CLI.Wizard.Environments;
using ScribeWench.CLI.Wizard.Steps;
using ScribeWench.Core.Models;
using ScribeWench.Tests.Support;

namespace ScribeWench.Tests;

public class SetupWizardTests
{
    // ─── A first run ─────────────────────────────────────────────────────────

    [Fact]
    public async Task FirstRun_WithClaudeCodeInstalled_IsATokenAndEnterAllTheWay()
    {
        var run = new WizardRun { Name = "first-run-claude-code" }.WithDiscord();
        run.Machine.ClaudeCode = "2.4.1 (Claude Code)";
        run.Pick(1)                 // let me choose
            .No()                   // open Discord's page in the browser?
            .Type(WizardRun.Token)
            .Enter()                // Whisper model: the suggested one
            .Enter()                // download it
            .Enter()                // who writes the notes: Claude Code
            .Enter()                // model: opus
            .Enter()                // notes folder
            .Enter()                // keep transcripts
            .Enter()                // post in Discord
            .Enter();               // save

        Assert.Equal(0, await run.RunAsync());

        var config = run.Saved;
        Assert.Equal(WizardRun.Token, config.Discord.BotToken);
        Assert.Equal("!scribe", config.Discord.CommandPrefix);
        Assert.Equal("large", config.Whisper.ModelSize);        // a 16 GB card
        Assert.Equal(0, config.Whisper.GpuDevice);
        Assert.Equal(["large"], run.Machine.WhisperDownloads);
        Assert.Equal("claudecode", config.Ai.Backend);
        Assert.Equal("opus", config.Ai.ClaudeCode.Model);
        Assert.Equal(ObsidianConfig.DefaultVaultPath, config.Obsidian.VaultPath);
        Assert.Contains("The bot is called ScribeWench", run.Screen);
        Assert.Contains("Message Content Intent is on", run.Screen);
        Assert.Contains("The bot is in: The Tavern", run.Screen);
        Assert.Contains("!scribe create-game", run.Screen);
        Assert.DoesNotContain(WizardRun.Token, run.Screen);
    }

    [Fact]
    public async Task FirstRun_WithoutClaudeCode_OnAStrongPc_SuggestsTheBestLocalModel_DownloadsAndTriesIt()
    {
        var run = new WizardRun { Name = "first-run-local-model" }.WithDiscord().WithOllama();
        run.Pick(1).No().Type(WizardRun.Token)
            .Enter().Enter()        // Whisper
            .Enter()                // who writes the notes: a local model, first on this PC
            .Enter()                // which model: the suggested one
            .Enter()                // download it now
            .Enter().Enter().Enter()// notes folder, transcripts, Discord
            .Enter();               // save

        Assert.Equal(0, await run.RunAsync());

        var config = run.Saved;
        Assert.Equal("ollama", config.Ai.Backend);
        Assert.Equal("qwen3.8:27b", config.Ai.Ollama.Model);
        Assert.Equal(65536, config.Ai.Ollama.ContextSize);
        Assert.False(config.Ai.Ollama.Think);
        Assert.Equal("qwen3.8:27b", run.Web.To("/api/pull").Single().Json.GetProperty("model").GetString());
        Assert.Contains("suits this PC", run.Screen);
        Assert.Contains("It runs.", run.Screen);
        Assert.Contains("59% of it on the graphics card", run.Screen);
        // Tried with the window it will be used with, then unloaded so the memory is free for a recording.
        Assert.Equal(65536, run.Web.To("/api/chat").Single().Json.GetProperty("options").GetProperty("num_ctx").GetInt32());
        Assert.Single(run.Web.To("/api/generate"));
    }

    [Fact]
    public async Task FirstRun_OnASmallPcWithoutClaudeCode_PutsAServiceFirst_AndSaysWhy()
    {
        var run = new WizardRun { Name = "first-run-small-pc" }.WithDiscord();
        run.Machine.RamGb = 15.4;
        run.Machine.Cards = [new("Intel(R) Iris(R) Xe Graphics", 0.1)];
        run.Pick(1).No().Type(WizardRun.Token)
            .Enter().Enter()        // Whisper: base, on the processor
            .Enter()                // who writes the notes: Claude with an API key comes first here
            .Type("sk-ant-test")
            .Enter()                // claude-opus-5-5
            .Enter().Enter().Enter()
            .Enter();

        Assert.Equal(0, await run.RunAsync());

        var config = run.Saved;
        Assert.Equal("base", config.Whisper.ModelSize);
        Assert.Equal(-1, config.Whisper.GpuDevice);
        Assert.Equal("anthropic", config.Ai.Backend);
        Assert.Equal("sk-ant-test", config.Ai.Anthropic.ApiKey);
        Assert.Equal("claude-opus-5-5", config.Ai.Anthropic.Model);
        Assert.Contains("no graphics card to speak of", run.Screen);
        Assert.Contains("this PC is small for one", run.Screen);
        Assert.DoesNotContain("sk-ant-test", run.Screen);
    }

    // ─── The Discord bot ─────────────────────────────────────────────────────

    [Fact]
    public async Task Discord_WrongToken_ThenIntentOff_ThenNotInAServer_AreEachCaughtAndWalkedThrough()
    {
        var run = new WizardRun { Name = "discord-problems" };
        var intentOn = false;
        var inServer = false;
        run.Web.On("GET", "https://discord.com/api/v10/", request => request.Authorization != $"Bot {WizardRun.Token}"
            ? FakeWeb.Json(new { message = "401: Unauthorized" }, HttpStatusCode.Unauthorized)
            : request.Url.EndsWith("/users/@me") ? FakeWeb.Json(new { id = WizardRun.BotId, username = "ScribeWench" })
            : request.Url.EndsWith("/applications/@me") ? FakeWeb.Json(new { id = WizardRun.BotId, flags = intentOn ? 1 << 19 : 0 })
            : FakeWeb.Json(inServer ? new[] { new { id = "1", name = "The Tavern" } } : []));
        run.Machine.ClaudeCode = "2.4.1 (Claude Code)";
        run.Machine.OnOpen = url =>
        {
            if (url.EndsWith("/bot")) intentOn = true;                  // the user flips the switch on that page
            if (url.Contains("oauth2/authorize")) inServer = true;      // the user adds the bot on that page
        };

        run.Pick(1)
            .Yes()                      // open Discord's page
            .Type("the-application-id-by-mistake")
            .Type(WizardRun.Token)
            .Enter().Enter()            // intent is off: open the page; "Switched on and saved?" yes
            .Pick(1)                    // not in a server: check without having added it
            .Pick(0)                    // then open the link
            .Pick(0)                    // and check
            .Enter().Enter()            // Whisper
            .Enter().Enter()            // Claude Code, opus
            .Enter().Enter().Enter()
            .Enter();

        Assert.Equal(0, await run.RunAsync());

        Assert.Equal(WizardRun.Token, run.Saved.Discord.BotToken);
        Assert.Contains("Discord doesn't accept that token", run.Screen);
        Assert.Contains("Message Content Intent is off", run.Screen);
        Assert.Contains("Message Content Intent is on", run.Screen);
        Assert.Contains("Discord says the bot isn't in a server yet", run.Screen);
        Assert.Contains("The bot is in: The Tavern", run.Screen);
        Assert.Equal(
            [
                "https://discord.com/developers/applications",
                $"https://discord.com/developers/applications/{WizardRun.BotId}/bot",
                // View Channel, Send Messages, Attach Files, Read Message History, Send Messages in Threads, Connect, Speak, Use Voice Activity
                $"https://discord.com/oauth2/authorize?client_id={WizardRun.BotId}&permissions=274914708480&scope=bot",
            ],
            run.Machine.Opened);
    }

    [Fact]
    public async Task Discord_WhenOffline_TheTokenCanStillBeSaved()
    {
        var run = new WizardRun { Name = "discord-offline" };       // no Discord set up: nothing answers
        run.Machine.ClaudeCode = "2.4.1 (Claude Code)";
        run.Pick(1).No().Type(WizardRun.Token)
            .Yes()                      // save it unchecked
            .Enter().Enter().Enter().Enter().Enter().Enter().Enter()
            .Enter();

        Assert.Equal(0, await run.RunAsync());

        Assert.Equal(WizardRun.Token, run.Saved.Discord.BotToken);
        Assert.Contains("Couldn't reach Discord", run.Screen);
    }

    [Fact]
    public async Task Discord_WithoutATokenYet_TheRestCanBeSetUp_AndSetupSaysWhatIsMissing()
    {
        var run = new WizardRun { Name = "discord-later" };
        run.Machine.ClaudeCode = "2.4.1 (Claude Code)";
        run.Pick(1).No()
            .Enter()                    // no token: do this later
            .Enter().Enter().Enter().Enter().Enter().Enter().Enter()
            .Enter();

        Assert.Equal(0, await run.RunAsync());

        Assert.Equal("", run.Saved.Discord.BotToken);
        Assert.Equal("claudecode", run.Saved.Ai.Backend);
        Assert.Contains("no token yet", run.Screen);
        Assert.Contains("The bot has no token yet", run.Screen);
        Assert.DoesNotContain("Start the bot with", run.Screen);
    }

    // ─── Local models ────────────────────────────────────────────────────────

    private static AppConfig Configured(Action<AppConfig>? change = null)
    {
        var config = new AppConfig
        {
            Discord = { BotToken = WizardRun.Token },
            Whisper = { ModelSize = "large", GpuDevice = 0 },
            Ai = { Backend = "claudecode" },
        };
        change?.Invoke(config);
        return config;
    }

    /// <summary>A run that starts from saved settings and goes straight to "Change who writes the session notes".</summary>
    private static WizardRun ChangingTheWriter(string name, Action<AppConfig>? change = null)
    {
        var run = new WizardRun { Name = name }.WithDiscord();
        run.Store.Existing = Configured(change);
        run.Machine.ClaudeCode = "2.4.1 (Claude Code)";
        return run.Pick(3);
    }

    [Fact]
    public async Task LocalModel_WhenOllamaIsNotInstalled_SendsTheUserToGetIt_AndCarriesOnWhenItIsThere()
    {
        var run = ChangingTheWriter("local-ollama-not-installed");
        run.Machine.OllamaIsInstalled = false;
        run.Machine.OnOpen = _ =>
        {
            run.Machine.OllamaIsInstalled = true;     // the user installs it, and it starts by itself
            run.WithOllama(downloaded: ["gemma4:26b"]);
        };
        run.Pick(1)                 // a local model
            .Enter()                // open the download page
            .Enter()                // "Installed?" yes
            .Pick(2)                // gemma4:26b, already downloaded
            .Enter();               // save

        Assert.Equal(0, await run.RunAsync());

        Assert.Equal("ollama", run.Saved.Ai.Backend);
        Assert.Equal("gemma4:26b", run.Saved.Ai.Ollama.Model);
        Assert.Equal(["https://ollama.com/download"], run.Machine.Opened);
        Assert.Contains("Ollama isn't installed on this PC", run.Screen);
        Assert.Empty(run.Web.To("/api/pull"));      // it was there already
    }

    [Fact]
    public async Task LocalModel_WhenOllamaIsInstalledButNotRunning_IsStartedWithoutAsking()
    {
        var run = ChangingTheWriter("local-ollama-not-running");
        run.Machine.OnStartOllama = () =>
        {
            run.WithOllama(downloaded: ["qwen3.8:27b"]);
            return true;
        };
        run.Pick(1).Enter().Enter();    // a local model; the suggested one; save

        Assert.Equal(0, await run.RunAsync());

        Assert.Equal("qwen3.8:27b", run.Saved.Ai.Ollama.Model);
        Assert.Contains("Ollama 0.40.18 is running", run.Screen);
    }

    [Fact]
    public async Task LocalModel_AnyOtherModelCanBeTypedIn_AndAMistypedNameLeadsBackToTheList()
    {
        var run = ChangingTheWriter("local-another-model").WithOllama(unknown: ["bonsai-2:72b"]);
        run.Pick(1)                 // a local model
            .Pick(4)                // another model
            .Type("bonsai-2:72b")
            .Enter()                // download it: Ollama has no such model
            .Pick(4)                // another model, again
            .Type("bonsai-2:27b")
            .Enter()                // download it
            .Yes()                  // advanced settings
            .Type("32768")
            .Yes()                  // let it think
            .Enter();               // save

        Assert.Equal(0, await run.RunAsync());

        var ollama = run.Saved.Ai.Ollama;
        Assert.Equal("bonsai-2:27b", ollama.Model);
        Assert.Equal(32768, ollama.ContextSize);
        Assert.True(ollama.Think);
        Assert.Contains("Ollama has no model called 'bonsai-2:72b'", run.Screen);
        Assert.Equal(32768, run.Web.To("/api/chat").Single().Json.GetProperty("options").GetProperty("num_ctx").GetInt32());
    }

    [Fact]
    public async Task LocalModel_ThatWillNotLoad_IsCaughtInSetup_NotAfterTheFirstSession()
    {
        var run = ChangingTheWriter("local-does-not-load")
            .WithOllama(downloaded: ["qwen3.8:27b-q8_0", "gemma4:12b"], loadError: "model requires more system memory (41.2 GiB) than is available (27.9 GiB)");
        run.Pick(1)                 // a local model
            .Pick(1)                // the higher-precision build
            .Enter()                // it didn't load: pick another
            .Pick(3)                // gemma4:12b
            .Pick(1)                // that fails too in this made-up Ollama: keep it anyway
            .Enter();

        Assert.Equal(0, await run.RunAsync());

        Assert.Equal("gemma4:12b", run.Saved.Ai.Ollama.Model);
        Assert.Contains("It didn't load:", run.Screen);
        Assert.Contains("more system memory", run.Screen);
        Assert.DoesNotContain("not tested by us", run.Screen);
    }

    [Fact]
    public async Task LocalModel_WithTooLittleDiskSpace_IsNotDownloaded()
    {
        var run = ChangingTheWriter("local-disk-full").WithOllama();
        run.Machine.FreeDiskGb = 12;
        run.Pick(1)                 // a local model
            .Enter()                // qwen3.8:27b: no room
            .Pick(3)                // gemma4:12b fits
            .Enter()                // download
            .Enter();

        Assert.Equal(0, await run.RunAsync());

        Assert.Equal("gemma4:12b", run.Saved.Ai.Ollama.Model);
        Assert.Contains("Not enough disk space", run.Screen);
        Assert.Equal("gemma4:12b", run.Web.To("/api/pull").Single().Json.GetProperty("model").GetString());
    }

    [Fact]
    public async Task LocalModel_OnAnotherComputer_IsReachedAtItsAddress()
    {
        var run = ChangingTheWriter("local-other-computer");
        run.Machine.OllamaIsInstalled = false;
        run.Web.On("GET", "http://192.168.1.50:11434/api/version", new { version = "0.40.18" })
            .On("GET", "http://192.168.1.50:11434/api/tags", new { models = new[] { new { name = "qwen3.8:27b", size = 17_700_000_000 } } })
            .On("POST", "http://192.168.1.50:11434/api/show", new { details = new { }, capabilities = new[] { "completion" } })
            .On("POST", "http://192.168.1.50:11434/api/chat", new { message = new { content = "ready" } })
            .On("GET", "http://192.168.1.50:11434/api/ps", new { models = Array.Empty<object>() })
            .On("POST", "http://192.168.1.50:11434/api/generate", new { });
        run.Pick(1)                 // a local model
            .Pick(2)                // Ollama runs on another computer
            .Type("http://192.168.1.50:11434/")
            .Pick(1)                // qwen3.8:27b (nothing is suggested for a machine setup can't see)
            .Enter();

        Assert.Equal(0, await run.RunAsync());

        Assert.Equal("http://192.168.1.50:11434", run.Saved.Ai.Ollama.BaseUrl);
        Assert.Equal("qwen3.8:27b", run.Saved.Ai.Ollama.Model);
        Assert.DoesNotContain("suits this PC", run.Screen);
    }

    // ─── Services ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Service_PickedFromTheList_GetsItsModelsFromTheServiceItself()
    {
        var run = ChangingTheWriter("service-deepseek");
        run.Web.On("GET", "https://api.deepseek.com/models", request => request.Authorization == "Bearer sk-deepseek"
            ? FakeWeb.Json(new { data = new[] { new { id = "deepseek-flash" }, new { id = "deepseek-v4-pro" } } })
            : FakeWeb.Json(new { error = new { message = "Authentication Fails" } }, HttpStatusCode.Unauthorized));
        run.Pick(3)                 // a service
            .Pick(2)                // DeepSeek
            .Type("sk-deepseek-typo")
            .Enter()                // rejected: enter the key again
            .Type("sk-deepseek")
            .Enter()                // the first model offered
            .Enter();

        Assert.Equal(0, await run.RunAsync());

        var openAi = run.Saved.Ai.OpenAi;
        Assert.Equal("openai", run.Saved.Ai.Backend);
        Assert.Equal("https://api.deepseek.com", openAi.BaseUrl);
        Assert.Equal("sk-deepseek", openAi.ApiKey);
        Assert.Equal("deepseek-v4-pro", openAi.Model);      // the service's main model first, not the alphabet's
        Assert.Equal(0, openAi.ContextSize);
        Assert.Contains("The key was rejected", run.Screen);
        Assert.DoesNotContain("sk-deepseek", run.Screen);
    }

    [Fact]
    public async Task Service_ThatSaysHowMuchItsModelsRead_HasASmallWindowSetUpForReadingInParts()
    {
        var run = ChangingTheWriter("service-openrouter");
        run.Web.On("GET", "https://openrouter.ai/api/v1/models", new
        {
            data = new object[]
            {
                new { id = "bigco/big-model", context_length = 1_048_576 },
                new { id = "smallco/small-model", context_length = 32_768 },
                new { id = "bigco/text-embedding-3", context_length = 8192 },
            },
        });
        run.Pick(3)                 // a service
            .Pick(1)                // OpenRouter
            .Type("or-key")
            .Pick(1)                // smallco/small-model
            .Enter();

        Assert.Equal(0, await run.RunAsync());

        var openAi = run.Saved.Ai.OpenAi;
        Assert.Equal("https://openrouter.ai/api/v1", openAi.BaseUrl);
        Assert.Equal("smallco/small-model", openAi.Model);
        Assert.Equal(32_768, openAi.ContextSize);
        Assert.Contains("will be read in parts", run.Screen);
        Assert.DoesNotContain("text-embedding-3", run.Screen);
    }

    [Fact]
    public async Task Service_OfYourOwn_NeedsOnlyAnAddress()
    {
        var run = ChangingTheWriter("service-own-server");
        run.Web.On("GET", "http://localhost:1234/v1/models", new { data = new[] { new { id = "my-local-model" } } });
        run.Pick(3)                 // a service
            .Pick(5)                // another service, or a server of your own
            .Type("http://localhost:1234/v1/")
            .Enter()                // no key
            .Enter()                // my-local-model
            .Type("16384")          // its context window
            .Enter();

        Assert.Equal(0, await run.RunAsync());

        var openAi = run.Saved.Ai.OpenAi;
        Assert.Equal("http://localhost:1234/v1", openAi.BaseUrl);
        Assert.Equal("", openAi.ApiKey);
        Assert.Equal("my-local-model", openAi.Model);
        Assert.Equal(16384, openAi.ContextSize);
        Assert.Null(run.Web.To("localhost:1234").Single().Authorization);
    }

    [Fact]
    public async Task Service_KeepingTheSavedKey_KeepsTheKey()
    {
        // The old wizard saved the text "(keep existing)" as the key here.
        var run = ChangingTheWriter("service-keep-key", config =>
        {
            config.Ai.Backend = "openai";
            config.Ai.OpenAi.ApiKey = "sk-saved";
            config.Ai.OpenAi.Model = "gpt-6-luna";
        });
        run.Web.On("GET", "https://api.openai.com/v1/models", new { data = new[] { new { id = "gpt-6.2-sol" }, new { id = "gpt-6-luna" }, new { id = "whisper-1" } } });
        run.Pick(3)                 // a service
            .Enter()                // OpenAI
            .Enter()                // keep the saved key
            .Enter()                // the model in use is offered first
            .Enter();

        Assert.Equal(0, await run.RunAsync());

        Assert.Equal("sk-saved", run.Saved.Ai.OpenAi.ApiKey);
        Assert.Equal("gpt-6-luna", run.Saved.Ai.OpenAi.Model);
        Assert.Equal("", run.Saved.Ai.OpenAi.BaseUrl);
        Assert.Equal("Bearer sk-saved", run.Web.To("api.openai.com").Single().Authorization);
    }

    // ─── Changing saved settings ─────────────────────────────────────────────

    [Fact]
    public async Task ExistingSettings_OpenOnAMenu_AndQuittingSavesNothing()
    {
        var run = new WizardRun { Name = "existing-quit" }.WithDiscord();
        run.Store.Existing = Configured();
        run.Pick(6);                // quit without saving

        Assert.Equal(1, await run.RunAsync());

        Assert.Null(run.Store.Saved);
        Assert.Contains("Claude opus, through Claude Code on this PC", run.Screen);
        Assert.DoesNotContain("Step 1 of 4", run.Screen);
    }

    [Fact]
    public async Task ExistingSettings_ChangingTheBot_KeepsTheTokenByDefault_AndOffersThePrefix()
    {
        var run = new WizardRun { Name = "existing-change-bot" }.WithDiscord(servers: ["The Tavern", "Test Server"]);
        run.Store.Existing = Configured();
        run.Pick(1)                 // change the Discord bot
            .Enter()                // use a different bot? no
            .Type("!notes")
            .Enter();               // save

        Assert.Equal(0, await run.RunAsync());

        Assert.Equal(WizardRun.Token, run.Saved.Discord.BotToken);
        Assert.Equal("!notes", run.Saved.Discord.CommandPrefix);
        Assert.Contains("The bot is in: The Tavern, Test Server", run.Screen);
        Assert.Contains("!notes create-game", run.Screen);
    }

    [Fact]
    public async Task BackingOutOfAWayToWriteNotes_LeadsBackToTheList()
    {
        var run = ChangingTheWriter("back-out");
        run.Machine.OllamaIsInstalled = false;
        run.Pick(1)                 // a local model: Ollama isn't installed
            .Pick(3)                // choose another way
            .Enter()                // Claude Code
            .Pick(1)                // sonnet
            .Enter();

        Assert.Equal(0, await run.RunAsync());

        Assert.Equal("claudecode", run.Saved.Ai.Backend);
        Assert.Equal("sonnet", run.Saved.Ai.ClaudeCode.Model);
    }

    [Fact]
    public async Task Transcription_WithASmallGraphicsCard_AsksWhetherItIsARealOne()
    {
        var run = new WizardRun { Name = "transcription-small-card" }.WithDiscord();
        run.Store.Existing = Configured(config => config.Whisper = new WhisperConfig { ModelSize = "base", GpuDevice = -1 });
        run.Machine.Cards = [new("NVIDIA GeForce GTX 1650", 4)];
        run.Pick(2)                 // change transcription
            .Yes()                  // it is a separate card
            .Enter()                // the model that suits a 4 GB card
            .Enter()                // download
            .Enter();               // save

        Assert.Equal(0, await run.RunAsync());

        Assert.Equal("medium", run.Saved.Whisper.ModelSize);
        Assert.Equal(0, run.Saved.Whisper.GpuDevice);
        Assert.Contains("NVIDIA GeForce GTX 1650, 4 GB", run.Screen);
    }

    // ─── The small decisions ─────────────────────────────────────────────────

    [Theory]
    [InlineData(15.8, "large")]
    [InlineData(8, "large")]
    [InlineData(6, "medium")]
    [InlineData(4, "medium")]
    [InlineData(2, "small")]
    [InlineData(0, "base")]
    public void WhisperModel_FollowsTheGraphicsCard(double cardGb, string expected)
    {
        Assert.Equal(expected, WhisperCatalog.Recommended(cardGb));
    }

    [Fact]
    public async Task Transcription_WithGraphicsBuiltIntoTheProcessor_StaysOnTheProcessor()
    {
        var run = new WizardRun { Name = "transcription-built-in-graphics" }.WithDiscord();
        run.Store.Existing = Configured();
        run.Machine.Cards = [new("AMD Radeon(TM) 890M Graphics", 3.8)];
        run.Pick(2)                 // change transcription
            .No()                   // not a separate card
            .Enter()                // base
            .Enter()                // download
            .Enter();

        Assert.Equal(0, await run.RunAsync());

        Assert.Equal("base", run.Saved.Whisper.ModelSize);
        Assert.Equal(-1, run.Saved.Whisper.GpuDevice);
        Assert.Contains("Transcription will run on the processor", run.Screen);
    }

    [Fact]
    public void Review_SaysWhoWritesTheNotes_InWords()
    {
        using var services = new WizardRun().Services();
        var summaries = services.GetRequiredService<SummariesStep>();

        Assert.Equal("qwen3.8:27b, a local model run by Ollama",
            summaries.Describe(new AiConfig { Backend = "ollama", Ollama = { Model = "qwen3.8:27b" } }));
        Assert.Equal("deepseek-v4-pro, at api.deepseek.com",
            summaries.Describe(new AiConfig { Backend = "openai", OpenAi = { Model = "deepseek-v4-pro", BaseUrl = "https://api.deepseek.com" } }));
        Assert.Equal("gpt-6.2-sol, at api.openai.com",
            summaries.Describe(new AiConfig { Backend = "openai", OpenAi = { Model = "gpt-6.2-sol" } }));
    }

    [Fact]
    public void MachineInfo_DetectsThisMachine_WithoutFailing()
    {
        var machine = MachineInfo.Detect();

        Assert.True(machine.RamGb > 1);
        Assert.All(machine.Cards, card => Assert.False(string.IsNullOrWhiteSpace(card.Name)));
    }
}
