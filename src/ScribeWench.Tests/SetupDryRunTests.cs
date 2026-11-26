using ScribeWench.CLI.Commands;
using ScribeWench.CLI.Wizard.Environments;
using ScribeWench.Core.Models;
using ScribeWench.Tests.Support;

namespace ScribeWench.Tests;

/// <summary>`setup --dry-run`: every screen, and nothing downloaded, installed, started or saved.</summary>
public class SetupDryRunTests
{
    /// <summary>Nothing reached the machine or the real Ollama that would change either.</summary>
    private static void AssertNothingWasDone(WizardRun run)
    {
        Assert.Null(run.Store.Saved);
        Assert.Empty(run.Machine.WhisperDownloads);
        Assert.Equal(0, run.Machine.OllamaInstalls);
        Assert.Empty(run.Machine.CreatedFolders);
        Assert.DoesNotContain(run.Web.Requests, r => r.Url.Contains("/api/pull") || r.Url.Contains("/api/chat") || r.Url.Contains("/api/generate"));
    }

    [Fact]
    public async Task AsABarePcWithAGoodCard_DoItForMe_ShowsEveryStep_AndDoesNoneOfThem()
    {
        var run = new WizardRun { Name = "dry-run-fresh-strong", DryRun = new DryRunOptions(Fresh: true, RamGb: 32, CardGb: 16) }.WithDiscord();
        // The real PC is nothing like the pretend one: saved settings, Claude Code, Ollama, a small card.
        run.Store.Existing = new AppConfig { Discord = { BotToken = WizardRun.Token }, Ai = { Backend = "claudecode" } };
        run.Machine.ClaudeCode = "2.4.1 (Claude Code)";
        run.Machine.Cards = [new("AMD Radeon(TM) 890M Graphics", 3.8)];
        run.Machine.Winget = true;
        run.Machine.MissingFolders.Add(PathHelper.Expand(ObsidianConfig.DefaultVaultPath));
        var started = 0;
        run.Machine.OnStartOllama = () => { started++; return true; };

        run.Enter()                     // do it for me
            .No()                       // open Discord's page?
            .Enter()                    // the token: later
            .Enter()                    // who writes the notes: a local model
            .Enter()                    // go ahead
            .Enter();                   // save and finish

        Assert.Equal(0, await run.RunAsync());

        AssertNothingWasDone(run);
        Assert.Equal(0, started);
        Assert.Contains("Dry run", run.Screen);
        Assert.Contains("Pretending: 32 GB of RAM, a graphics card with 16 GB, nothing set up or installed yet.", run.Screen);
        Assert.Contains("Step 1 of 2", run.Screen);                                 // a first run, saved settings or not
        Assert.Contains("install Ollama (2 GB), download 18 GB", run.Screen);
        Assert.Contains("download 3.1 GB", run.Screen);
        Assert.Contains("(dry run) here winget downloads Ollama", run.Screen);
        Assert.Contains("Ollama is installed.", run.Screen);
        Assert.Contains("Downloading qwen3.8:27b (18 GB).", run.Screen);
        Assert.Contains("It runs.", run.Screen);
        Assert.Contains("61% of it on the graphics card", run.Screen);              // it was 59% on the real card, of 15.8 GB
        Assert.Contains("qwen3.8:27b, a local model run by Ollama", run.Screen);
        Assert.Contains("Dry run: nothing was saved.", run.Screen);
        Assert.DoesNotContain("Saved:", run.Screen);
    }

    [Fact]
    public async Task OnThePcAsItIs_WhatIsReallyThereIsShown_AndADownloadIsOnlyActedOut()
    {
        var run = new WizardRun { Name = "dry-run-as-is", DryRun = new DryRunOptions() }
            .WithDiscord()
            .WithOllama(downloaded: ["gemma4:12b"]);
        run.Store.Existing = new AppConfig { Discord = { BotToken = WizardRun.Token }, Ai = { Backend = "claudecode" } };
        run.Machine.ClaudeCode = "2.4.1 (Claude Code)";

        run.Pick(3)                     // change who writes the session notes
            .Pick(1)                    // a local model
            .Enter()                    // qwen3.8:27b, which isn't downloaded
            .Enter()                    // download it
            .Pick(3)                    // change who writes the session notes, again
            .Pick(1)
            .Enter()                    // qwen3.8:27b: now it counts as downloaded, so no question about that
            .Enter();                   // save and finish

        Assert.Equal(0, await run.RunAsync());

        AssertNothingWasDone(run);
        Assert.Contains("Ollama 0.40.18 is running.", run.Screen);                  // the real one answered
        Assert.Contains("gemma4:12b", run.Screen);
        Assert.Equal(1, CountOf(run.Screen, "Download qwen3.8:27b now (18 GB)?"));
        Assert.Equal(2, CountOf(run.Screen, "It runs."));
        Assert.DoesNotContain("Pretending:", run.Screen);
    }

    [Fact]
    public async Task ModelTooLargeForThePretendPc_FailsToLoad_AsItWould()
    {
        var run = new WizardRun { Name = "dry-run-too-large", DryRun = new DryRunOptions(Fresh: true, RamGb: 16, CardGb: 8) }.WithDiscord();
        run.Machine.Winget = true;

        run.Pick(1)                     // let me choose
            .No().Enter()               // the bot: later
            .Enter().Enter()            // Whisper, download
            .Enter()                    // a local model comes first: gemma4:12b suits this PC
            .Enter()                    // install Ollama for me
            .Pick(1)                    // the 30 GB one instead of the suggested one
            .Enter()                    // download
            .Enter()                    // it didn't load: pick another
            .Enter()                    // gemma4:12b
            .Enter()                    // download
            .Enter().Enter().Enter()    // notes folder, transcripts, Discord
            .Enter();                   // save and finish

        Assert.Equal(0, await run.RunAsync());

        AssertNothingWasDone(run);
        Assert.Contains("It didn't load:", run.Screen);
        Assert.Contains("model requires more system memory (35.0 GiB) than is available (24.0 GiB)", run.Screen);
        Assert.Contains("gemma4:12b, a local model run by Ollama", run.Screen);
    }

    [Fact]
    public void ThePretendSwitches_OnlyWorkInADryRun()
    {
        Assert.True(new SetupCommandSettings { DryRun = true, Fresh = true, Ram = 32, Card = 0 }.Validate().Successful);
        Assert.True(new SetupCommandSettings().Validate().Successful);

        Assert.False(new SetupCommandSettings { Fresh = true }.Validate().Successful);
        Assert.False(new SetupCommandSettings { Ram = 32 }.Validate().Successful);
        Assert.False(new SetupCommandSettings { Card = 16 }.Validate().Successful);
        Assert.False(new SetupCommandSettings { DryRun = true, Ram = 0 }.Validate().Successful);
    }

    private static int CountOf(string text, string part)
    {
        var count = 0;
        for (var at = text.IndexOf(part, StringComparison.Ordinal); at >= 0; at = text.IndexOf(part, at + part.Length, StringComparison.Ordinal))
            count++;
        return count;
    }
}
