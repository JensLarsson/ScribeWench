using ScribeWench.CLI.Wizard;
using ScribeWench.CLI.Wizard.Environments;
using ScribeWench.Core.Models;
using ScribeWench.Tests.Support;

namespace ScribeWench.Tests;

/// <summary>What setup opens with: the scribe on a first run where she fits and can be drawn, the name in large letters everywhere else.</summary>
public class BannerTests
{
    private const string Name = "S c r i b e W e n c h";
    private const string LargeLetters = @"|____/   \___| |_|";
    private const string Tagline = "Records your tabletop sessions on Discord and writes the session notes.";

    /// <summary>A first run that sets up everything but the bot's token, in a window of the given size.</summary>
    private static WizardRun FirstRun(string name, int width = 120, int height = 30)
    {
        var run = new WizardRun { Name = name };
        run.Console.Profile.Width = width;
        run.Console.Profile.Height = height;
        run.Machine.ClaudeCode = "2.4.1 (Claude Code)";
        run.Pick(1).No()
            .Enter()                    // no token: do this later
            .Enter().Enter().Enter().Enter().Enter().Enter().Enter()
            .Enter();
        return run;
    }

    /// <summary>The screen from its top down to the first empty row.</summary>
    private static string[] Top(WizardRun run) =>
        run.Screen[(run.Screen.IndexOf('H') + 1)..].Split('\n').TakeWhile(row => row.Trim().Length > 0).ToArray();

    private static bool HasDots(string row) => row.Any(c => c is >= '⠀' and <= '⣿');

    private static void AssertLargeLetters(WizardRun run)
    {
        Assert.Contains(LargeLetters, run.Screen);
        Assert.Contains(Tagline, run.Screen);
        Assert.DoesNotContain(Name, run.Screen);
        Assert.DoesNotContain(Top(run), HasDots);
    }

    [Fact]
    public async Task FirstRun_OpensWithTheScribe_AndTheNameInTheCornerAboveHerQuill()
    {
        var run = FirstRun("banner-first-run");

        Assert.Equal(0, await run.RunAsync());

        var rows = Top(run);
        Assert.Equal(Mascot.Height, rows.Length);
        Assert.All(rows, row => Assert.True(HasDots(row)));
        Assert.Equal(Mascot.Corner, rows[2].IndexOf(Name, StringComparison.Ordinal));
        Assert.Equal(Mascot.Corner, rows[4].IndexOf("Records your tabletop sessions on Discord", StringComparison.Ordinal));
        Assert.Equal(Mascot.Corner, rows[5].IndexOf("and writes the session notes.", StringComparison.Ordinal));
        Assert.All(rows, row => Assert.True(row.Length < 80));
        Assert.DoesNotContain(LargeLetters, run.Screen);
        Assert.DoesNotContain(Tagline, run.Screen);
        Assert.Contains("Setup needs one thing from you", run.Screen);
    }

    [Fact]
    public async Task FirstRun_InALowWindow_CutsHerShortAtTheBottom_SoTheFirstQuestionStillFits()
    {
        // Under her: an empty row, one line of text, and the question's four rows.
        var run = FirstRun("banner-low-window", height: 22);

        Assert.Equal(0, await run.RunAsync());

        var rows = Top(run);
        Assert.Equal(22 - 6, rows.Length);
        Assert.Contains(Name, rows[2]);
    }

    [Fact]
    public async Task FirstRun_InAWindowTooLowForHerHand_KeepsTheNameInLargeLetters()
    {
        var run = FirstRun("banner-too-low", height: Mascot.Least + 5);

        Assert.Equal(0, await run.RunAsync());

        AssertLargeLetters(run);
    }

    [Fact]
    public async Task FirstRun_InAWindowNarrowerThanEightyColumns_KeepsTheNameInLargeLetters()
    {
        var run = FirstRun("banner-narrow", width: 79);

        Assert.Equal(0, await run.RunAsync());

        AssertLargeLetters(run);
    }

    [Fact]
    public async Task FirstRun_AtEightyColumns_CountsTheRowsThatWrap()
    {
        // At this width the line under her and both answers take two rows each: three more than at 120.
        var run = FirstRun("banner-eighty-columns", width: 80, height: 26);

        Assert.Equal(0, await run.RunAsync());

        Assert.Equal(26 - 9, Top(run).Length);
    }

    [Fact]
    public async Task FirstRun_WhereBrailleIsNotDrawn_KeepsTheNameInLargeLetters()
    {
        var run = FirstRun("banner-no-braille");
        run.Terminal.DrawsBraille = false;

        Assert.Equal(0, await run.RunAsync());

        AssertLargeLetters(run);
    }

    [Fact]
    public async Task FirstRun_AsADryRun_MakesRoomForTheNoticeToo()
    {
        var run = new WizardRun { Name = "banner-dry-run", DryRun = new DryRunOptions(Fresh: true, RamGb: 32, CardGb: 16) }.WithDiscord();
        run.Console.Profile.Width = 120;
        run.Console.Profile.Height = 28;
        run.Machine.Winget = true;
        run.Enter()                     // do it for me
            .No()                       // open Discord's page?
            .Enter()                    // the token: later
            .Enter()                    // who writes the notes: a local model
            .Enter()                    // go ahead
            .Enter();                   // save and finish

        Assert.Equal(0, await run.RunAsync());

        // Under her: an empty row, the notice (a rule, three lines, an empty row), a line of text, the question.
        Assert.Equal(28 - 11, Top(run).Length);
        Assert.Contains("Dry run", run.Screen);
    }

    [Fact]
    public async Task LaterRun_KeepsTheNameInLargeLetters()
    {
        var run = new WizardRun { Name = "banner-later-run" }.WithDiscord();
        run.Console.Profile.Height = 60;
        run.Store.Existing = new AppConfig { Discord = { BotToken = WizardRun.Token }, Ai = { Backend = "claudecode" } };
        run.Pick(6);                // quit without saving

        Assert.Equal(1, await run.RunAsync());

        AssertLargeLetters(run);
    }
}
