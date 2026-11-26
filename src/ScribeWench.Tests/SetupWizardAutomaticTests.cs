using System.Net;
using ScribeWench.Core;
using ScribeWench.Core.Models;
using ScribeWench.Tests.Support;

namespace ScribeWench.Tests;

/// <summary>
/// "Do it for me": setup looks at the PC, asks who writes the notes, shows the plan once, and
/// carries it out.
/// </summary>
public class SetupWizardAutomaticTests
{
    private const string Claude = "2.4.1 (Claude Code)";

    /// <summary>A first run that picks "Do it for me" and gives the bot's token; what follows is up to the test.</summary>
    private static WizardRun FirstRun(string name)
    {
        var run = new WizardRun { Name = name }.WithDiscord();
        return run.Enter()              // do it for me
            .No()                       // open Discord's page in the browser?
            .Type(WizardRun.Token);
    }

    // ─── A local model ───────────────────────────────────────────────────────

    [Fact]
    public async Task OnAPcWithNothingInstalled_ALocalModel_MeansOllamaInstalled_TheModelDownloadedAndTried()
    {
        var run = FirstRun("auto-local-from-nothing");
        var notesFolder = PathHelper.Expand(ObsidianConfig.DefaultVaultPath);
        run.Machine.MissingFolders.Add(notesFolder);
        run.Machine.OllamaIsInstalled = false;
        run.Machine.Winget = true;
        run.Machine.OnInstallOllama = () =>
        {
            run.Machine.OllamaIsInstalled = true;
            run.WithOllama();
            return null;
        };
        run.Enter()                     // who writes the notes: a local model, first where there is no Claude Code
            .Enter()                    // go ahead
            .Enter();                   // save

        Assert.Equal(0, await run.RunAsync());

        var config = run.Saved;
        Assert.Equal("large", config.Whisper.ModelSize);
        Assert.Equal(0, config.Whisper.GpuDevice);
        Assert.Equal(["large"], run.Machine.WhisperDownloads);
        Assert.Equal("ollama", config.Ai.Backend);
        Assert.Equal("qwen3.8:27b", config.Ai.Ollama.Model);
        Assert.Equal(65536, config.Ai.Ollama.ContextSize);
        Assert.Equal(1, run.Machine.OllamaInstalls);
        Assert.Equal("qwen3.8:27b", run.Web.To("/api/pull").Single().Json.GetProperty("model").GetString());
        Assert.Single(run.Web.To("/api/chat"));                 // loaded once, to see that it runs
        Assert.Equal([notesFolder], run.Machine.CreatedFolders);
        Assert.True(config.Output.PostToDiscord);

        // Local or a service was the user's to say, and the plan said the rest before anything happened.
        Assert.Contains("Who should write the session notes?", run.Screen);
        Assert.Contains("A local model on this PC", run.Screen);
        Assert.Contains("A service you have an account with", run.Screen);
        Assert.DoesNotContain("Claude, through Claude Code", run.Screen);
        Assert.Contains("Whisper large, on the graphics card", run.Screen);
        Assert.Contains("download 3.1 GB", run.Screen);
        Assert.Contains("qwen3.8:27b, a local model run by Ollama", run.Screen);
        Assert.Contains("install Ollama (2 GB), download 18 GB", run.Screen);
        Assert.True(run.Screen.IndexOf("Go ahead?") < run.Screen.IndexOf("Installing Ollama"));
        Assert.Contains("It runs.", run.Screen);
        Assert.DoesNotContain("Which model?", run.Screen);
    }

    [Fact]
    public async Task ModelThatWillNotLoadOnThisPc_GivesWayToTheNextSizeDown()
    {
        var run = FirstRun("auto-next-size-down").WithOllama(tooLarge: ["qwen3.8:27b"]);
        run.Enter().Enter().Enter();    // a local model; go ahead; save

        Assert.Equal(0, await run.RunAsync());

        Assert.Equal("gemma4:26b", run.Saved.Ai.Ollama.Model);
        Assert.Equal(["qwen3.8:27b", "gemma4:26b"], run.Web.To("/api/pull").Select(r => r.Json.GetProperty("model").GetString()));
        Assert.Contains("It didn't load:", run.Screen);
        Assert.Contains("Trying the next size down.", run.Screen);
    }

    [Fact]
    public async Task WithTooLittleDiskForTheBestModel_ASmallerOneIsPlanned_AndThePlanSaysWhy()
    {
        var run = FirstRun("auto-disk").WithOllama();
        run.Machine.FreeDiskGb = 15;
        run.Enter().Enter().Enter();

        Assert.Equal(0, await run.RunAsync());

        Assert.Equal("gemma4:12b", run.Saved.Ai.Ollama.Model);
        Assert.Contains("qwen3.8:27b would write better notes here, but needs 20 GB of free disk and there are 15", run.Screen);
    }

    [Fact]
    public async Task WithNoDiskForAnyLocalModel_ThatIsSaid_AndTheOtherWaysRemain()
    {
        var run = FirstRun("auto-disk-full").WithOllama();
        run.Machine.FreeDiskGb = 5;
        run.Enter()                     // a local model: there turns out to be no room for one
            .Enter()                    // which leaves a service: Claude with an API key
            .Type("sk-ant-test")
            .Enter()                    // claude-opus-5-5
            .Enter()                    // go ahead
            .Enter();                   // save

        Assert.Equal(0, await run.RunAsync());

        Assert.Equal("anthropic", run.Saved.Ai.Backend);
        // Said when it turns out, and again under the plan, where the reason for the plan is looked for.
        Assert.Equal(2, CountOf(run.Screen, "its disk is too full: gemma4:12b needs 10 GB free and there are 5"));
        Assert.Empty(run.Web.To("/api/pull"));
    }

    [Fact]
    public async Task WhenInstallingOllamaFails_TheUserGetsTheOtherWaysIn_AndCanStillFinish()
    {
        var run = FirstRun("auto-install-fails");
        run.Machine.OllamaIsInstalled = false;
        run.Machine.Winget = true;      // and its install fails, as the made-up machine has it by default
        run.Machine.OnOpen = _ =>
        {
            run.Machine.OllamaIsInstalled = true;
            run.WithOllama();
        };
        run.Enter()                     // a local model
            .Enter()                    // go ahead
            .Pick(1)                    // installing failed: open the download page instead
            .Enter()                    // "Installed?" yes
            .Enter();                   // save

        Assert.Equal(0, await run.RunAsync());

        Assert.Equal("qwen3.8:27b", run.Saved.Ai.Ollama.Model);
        Assert.Equal(1, run.Machine.OllamaInstalls);
        Assert.Contains("Installing it didn't work:", run.Screen);
        Assert.Contains("0x8A150014", run.Screen);
    }

    [Fact]
    public async Task WithoutWinget_ThePlanSaysOllamaIsTheUsersToInstall()
    {
        var run = FirstRun("auto-no-winget");
        run.Machine.OllamaIsInstalled = false;
        run.Machine.OnOpen = _ =>
        {
            run.Machine.OllamaIsInstalled = true;
            run.WithOllama();
        };
        run.Enter()                     // a local model
            .Enter()                    // go ahead
            .Enter()                    // open the download page
            .Enter()                    // "Installed?" yes
            .Enter();

        Assert.Equal(0, await run.RunAsync());

        Assert.Equal("qwen3.8:27b", run.Saved.Ai.Ollama.Model);
        Assert.Equal(0, run.Machine.OllamaInstalls);
        Assert.Contains("you install Ollama (setup shows how)", run.Screen);
        Assert.DoesNotContain("Install it for me", run.Screen);
    }

    // ─── Claude Code, where it is found ──────────────────────────────────────

    [Fact]
    public async Task ClaudeCodeThatIsSignedIn_IsOfferedFirst_AndTakingItInstallsNothing()
    {
        var run = FirstRun("auto-claude-code");
        run.Machine.ClaudeCode = Claude;
        run.Machine.OllamaIsInstalled = false;
        run.Machine.Winget = true;
        run.Enter()                     // who writes the notes: Claude Code, first where it is signed in
            .Enter()                    // go ahead
            .Enter();                   // save

        Assert.Equal(0, await run.RunAsync());

        Assert.Equal("claudecode", run.Saved.Ai.Backend);
        Assert.Equal("opus", run.Saved.Ai.ClaudeCode.Model);
        Assert.Equal(0, run.Machine.OllamaInstalls);
        Assert.Empty(run.Web.To(":11434"));
        Assert.Contains("your Claude subscription (dm@example.com)", run.Screen);
        Assert.Contains("A local model on this PC", run.Screen);        // still the user's to pick
        Assert.Contains("Claude opus, through Claude Code on this PC", run.Screen);
    }

    [Fact]
    public async Task WithClaudeCodeThere_ALocalModelCanStillBeChosen()
    {
        var run = FirstRun("auto-local-over-claude").WithOllama(downloaded: ["qwen3.8:27b"]);
        run.Machine.ClaudeCode = Claude;
        run.Pick(1)                     // a local model, second after Claude Code
            .Enter()                    // go ahead
            .Enter();

        Assert.Equal(0, await run.RunAsync());

        Assert.Equal("ollama", run.Saved.Ai.Backend);
        Assert.Equal("qwen3.8:27b", run.Saved.Ai.Ollama.Model);
    }

    [Fact]
    public async Task ClaudeCodeThatIsNotSignedIn_IsNotOffered_AndSetupSaysHowToGetIt()
    {
        var run = FirstRun("auto-claude-code-signed-out").WithOllama(downloaded: ["qwen3.8:27b"]);
        run.Machine.ClaudeCode = Claude;
        run.Machine.ClaudeAccount = new(false, null, null, null);
        run.Machine.WhisperModelsOnDisk.Add("large");
        run.Enter().Enter().Enter();    // a local model; go ahead; save

        Assert.Equal(0, await run.RunAsync());

        Assert.Equal("ollama", run.Saved.Ai.Backend);
        Assert.Contains("Claude Code is on this PC but not signed in", run.Screen);
        Assert.DoesNotContain("Claude, through Claude Code on this PC", run.Screen);
        // Nothing left to fetch, and the plan says that too.
        Assert.Empty(run.Machine.WhisperDownloads);
        Assert.Empty(run.Web.To("/api/pull"));
        Assert.Equal(2, CountOf(run.Screen, "already downloaded"));
    }

    [Fact]
    public async Task ALoginBilledPerUse_IsOffered_ButNotFirst()
    {
        var run = FirstRun("auto-claude-code-api-key").WithOllama(downloaded: ["qwen3.8:27b"]);
        run.Machine.ClaudeCode = Claude;
        run.Machine.ClaudeAccount = new(true, "apiKey", null, null);
        run.Enter().Enter().Enter();    // the first choice is the local model

        Assert.Equal(0, await run.RunAsync());

        Assert.Equal("ollama", run.Saved.Ai.Backend);
        Assert.Contains("its login here is billed per use", run.Screen);
    }

    // ─── A service ───────────────────────────────────────────────────────────

    [Fact]
    public async Task AService_IsSetUpWithItsOwnQuestions_AndThenShownInThePlan()
    {
        var run = FirstRun("auto-service");
        run.Web.On("GET", "https://api.deepseek.com/models", new { data = new[] { new { id = "deepseek-flash" }, new { id = "deepseek-v4-pro" } } });
        run.Pick(1)                     // who writes the notes: a service (second, after the local model)
            .Pick(1)                    // OpenAI, DeepSeek and the others
            .Pick(2)                    // DeepSeek
            .Type("sk-deepseek")
            .Enter()                    // the first model offered
            .Enter()                    // go ahead
            .Enter();                   // save

        Assert.Equal(0, await run.RunAsync());

        Assert.Equal("openai", run.Saved.Ai.Backend);
        Assert.Equal("https://api.deepseek.com", run.Saved.Ai.OpenAi.BaseUrl);
        Assert.Equal("deepseek-v4-pro", run.Saved.Ai.OpenAi.Model);
        Assert.Equal("large", run.Saved.Whisper.ModelSize);
        Assert.Contains("deepseek-v4-pro, at api.deepseek.com", run.Screen);
        Assert.Empty(run.Web.To(":11434"));
    }

    [Fact]
    public async Task GoingBackFromTheServices_LeadsBackToTheChoice()
    {
        var run = FirstRun("auto-service-back");
        run.Machine.ClaudeCode = Claude;
        run.Pick(2)                     // a service (third, after Claude Code and the local model)
            .Pick(2)                    // back
            .Enter()                    // Claude Code after all
            .Enter()                    // go ahead
            .Enter();

        Assert.Equal(0, await run.RunAsync());

        Assert.Equal("claudecode", run.Saved.Ai.Backend);
        // Asked, and after going back asked again.
        Assert.Contains("Who should write the session notes? A service you have an account with", run.Screen);
        Assert.Contains("Who should write the session notes? Claude, through Claude Code on this PC", run.Screen);
    }

    [Fact]
    public async Task OnAPcTooSmallForALocalModel_WithoutClaudeCode_OnlyAServiceIsLeft_AndThatIsSaidNotAsked()
    {
        var run = FirstRun("auto-small-pc");
        run.Machine.RamGb = 15.4;
        run.Machine.Cards = [new("Intel(R) Iris(R) Xe Graphics", 0.1)];
        run.Enter()                     // which service: Claude with an API key
            .Type("sk-ant-test")
            .Enter()                    // claude-opus-5-5
            .Enter()                    // go ahead
            .Enter();                   // save

        Assert.Equal(0, await run.RunAsync());

        Assert.Equal("base", run.Saved.Whisper.ModelSize);
        Assert.Equal(-1, run.Saved.Whisper.GpuDevice);
        Assert.Equal("anthropic", run.Saved.Ai.Backend);
        Assert.Contains("It is small for a local model", run.Screen);
        Assert.Contains("That leaves a service you have an account with", run.Screen);
        Assert.DoesNotContain("Who should write the session notes?", run.Screen);
    }

    // ─── The plan, and the menu ──────────────────────────────────────────────

    [Fact]
    public async Task TurningThePlanDown_LeadsToTheStepsOneAtATime_WithNothingDownloadedYet()
    {
        var run = FirstRun("auto-declined").WithOllama();
        run.Machine.ClaudeCode = Claude;
        run.Pick(1)                     // a local model
            .No()                       // go ahead? no
            .Pick(1)                    // Whisper: medium
            .Enter()                    // download
            .Enter()                    // Claude Code
            .Pick(1)                    // sonnet
            .Enter().Enter().Enter()    // notes folder, transcripts, Discord
            .Enter();                   // save

        Assert.Equal(0, await run.RunAsync());

        Assert.Equal("medium", run.Saved.Whisper.ModelSize);
        Assert.Equal(["medium"], run.Machine.WhisperDownloads);
        Assert.Equal("sonnet", run.Saved.Ai.ClaudeCode.Model);
        Assert.Empty(run.Web.To("/api/pull"));
        Assert.Contains("Step 2 of 4: Transcription", run.Screen);
    }

    [Fact]
    public async Task FromTheMenu_ItRedoesTranscriptionAndTheWriter_AndLeavesTheNotesSettingsAlone()
    {
        var run = new WizardRun { Name = "auto-from-menu" }.WithDiscord().WithOllama(downloaded: ["qwen3.8:27b"]);
        run.Store.Existing = new AppConfig
        {
            Discord = { BotToken = WizardRun.Token },
            Whisper = { ModelSize = "base", GpuDevice = -1 },
            Ai = { Backend = "llamasharp" },
            Obsidian = { VaultPath = @"D:\Vault\TTRPG" },
            Output = { PostToDiscord = false },
        };
        run.Pick(5)                     // do it for me
            .Enter()                    // a local model
            .Enter()                    // go ahead
            .Enter();                   // save

        Assert.Equal(0, await run.RunAsync());

        var config = run.Saved;
        Assert.Equal("large", config.Whisper.ModelSize);
        Assert.Equal("ollama", config.Ai.Backend);
        Assert.Equal("qwen3.8:27b", config.Ai.Ollama.Model);
        Assert.Equal(@"D:\Vault\TTRPG", config.Obsidian.VaultPath);
        Assert.False(config.Output.PostToDiscord);
        Assert.Empty(run.Machine.CreatedFolders);
    }

    [Fact]
    public async Task ChoosingALocalModelByHand_OffersToInstallOllama_WhereItIsMissing()
    {
        var run = new WizardRun { Name = "local-install-for-me" }.WithDiscord();
        run.Store.Existing = new AppConfig { Discord = { BotToken = WizardRun.Token }, Ai = { Backend = "claudecode" } };
        run.Machine.ClaudeCode = Claude;
        run.Machine.OllamaIsInstalled = false;
        run.Machine.Winget = true;
        run.Machine.OnInstallOllama = () =>
        {
            run.Machine.OllamaIsInstalled = true;
            run.WithOllama();
            return null;
        };
        run.Pick(3)                     // change who writes the session notes
            .Pick(1)                    // a local model
            .Enter()                    // install it for me
            .Enter()                    // the suggested model
            .Enter()                    // download it
            .Enter();                   // save

        Assert.Equal(0, await run.RunAsync());

        Assert.Equal("qwen3.8:27b", run.Saved.Ai.Ollama.Model);
        Assert.Equal(1, run.Machine.OllamaInstalls);
        Assert.Empty(run.Machine.Opened);
        Assert.Contains("Ollama is installed.", run.Screen);
    }

    private static int CountOf(string text, string part)
    {
        var count = 0;
        for (var at = text.IndexOf(part, StringComparison.Ordinal); at >= 0; at = text.IndexOf(part, at + part.Length, StringComparison.Ordinal))
            count++;
        return count;
    }
}
