using System.Text;
using ScribeWench.Tests.Support;
using ScribeWench.Core.Models;

namespace ScribeWench.Tests;

public sealed class InstructionsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "scribewench-tests", Guid.NewGuid().ToString("N"));
    private InstructionsStore Store(DateTime? now = null) => TestNotes.Instructions(
        new AppConfig { Ai = { PromptFile = DefaultPrompt } }, _root, now is { } at ? new FixedTime(at) : null);
    private string DefaultPrompt => Path.Combine(_root, "summary-prompt.md");

    public InstructionsTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private const string Valid = """
        <!-- my notes -->
        # Session note

        Write it like a pirate.

        ## What Happened
        [story]

        # Cast updates

        - Only the DM's name.

        # Vault updates

        - Only places.
        """;

    private static Instructions.Review Upload(string text, string name = "Runeterra.md") =>
        Instructions.ReviewUpload(name, Encoding.UTF8.GetBytes(text));

    // ── the file ─────────────────────────────────────────────────────────────

    [Fact]
    public void DefaultFile_ReadsBackAsTheDefaults()
    {
        var parsed = Instructions.Parse(Instructions.Default.ToFile());

        Assert.Empty(parsed.Problems);
        Assert.DoesNotContain(Instructions.Compare(Instructions.Default, parsed.Instructions!), c => c.Changed);
    }

    [Fact]
    public void ValidUpload_GivesEachSection_WithoutComments()
    {
        var review = Upload(Valid);

        Assert.True(review.Ok, string.Join(" ", review.Problems));
        Assert.StartsWith("Write it like a pirate.", review.Instructions!.SessionNote);
        Assert.Contains("## What Happened", review.Instructions.SessionNote);
        Assert.Equal("- Only the DM's name.", review.Instructions.CastUpdates);
        Assert.Equal("- Only places.", review.Instructions.VaultUpdates);
        Assert.Equal(Valid, review.Text);    // saved as uploaded, comments included
    }

    [Fact]
    public void Headings_IgnoreCaseAndTrailingColon_ButNotInsideCodeBlocks()
    {
        var text = "# SESSION NOTE:\nA\n```\n# Not a section\n```\n# cast updates\nB\n# Vault Updates\nC\n";

        var review = Upload(text);

        Assert.True(review.Ok, string.Join(" ", review.Problems));
        Assert.Contains("# Not a section", review.Instructions!.SessionNote);
    }

    [Theory]
    [InlineData("notes.txt", "It isn't a `.md` file.")]
    [InlineData("notes.MD", null)]
    public void Upload_MustBeMarkdownFile(string name, string? problem)
    {
        var review = Upload(Valid, name);
        if (problem is null) Assert.True(review.Ok);
        else Assert.Equal([problem], review.Problems);
    }

    [Fact]
    public void Upload_RejectsNonUtf8_Binary_Oversized_AndEmpty()
    {
        Assert.Contains("UTF-8", Instructions.ReviewUpload("a.md", [0x23, 0x20, 0xC3, 0x28]).Problems.Single());
        Assert.Contains("binary", Instructions.ReviewUpload("a.md", Encoding.UTF8.GetBytes("# Session note\0")).Problems.Single());
        Assert.Contains("KB", Instructions.ReviewUpload("a.md", new byte[Instructions.MaxFileBytes + 1]).Problems.Single());
        Assert.Contains("no instructions", Upload("<!-- only a comment -->").Problems.Single());
    }

    [Fact]
    public void Upload_WithByteOrderMark_IsFine()
    {
        var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true).GetPreamble().Concat(Encoding.UTF8.GetBytes(Valid)).ToArray();
        Assert.True(Instructions.ReviewUpload("a.md", bytes).Ok);
    }

    [Fact]
    public void Upload_ReportsEveryProblem()
    {
        var text = """
            Stray text.
            # Session note
            ## Vault Updates
            # Cast updates
            # Cast updates
            x
            # Lore
            y
            <!-- never closed
            """;

        var problems = Upload(text).Problems;

        Assert.Contains(problems, p => p.Contains("text before the first section"));
        Assert.Contains(problems, p => p.Contains("`## Vault Updates`, which ScribeWench adds itself"));
        Assert.Contains(problems, p => p.Contains("`# Cast updates` appears twice"));
        Assert.Contains(problems, p => p.Contains("Unknown section `# Lore`"));
        Assert.Contains(problems, p => p.Contains("Missing section `# Vault updates`"));
        Assert.Contains(problems, p => p.Contains("never closed with `-->`"));
        Assert.DoesNotContain(problems, p => p.Contains("`# Session note` is empty"));     // it holds the reserved heading line
    }

    [Fact]
    public void Upload_EmptySection_AndUnclosedCodeBlock()
    {
        var problems = Upload("# Session note\n```\nx\n# Cast updates\nB\n# Vault updates\nC\n").Problems;

        Assert.Contains(problems, p => p.Contains("code block is opened but never closed"));
        // Inside the open code block, the later headings aren't sections.
        Assert.Contains(problems, p => p.Contains("Missing section `# Cast updates`"));

        Assert.Contains("`# Cast updates` is empty.", Upload("# Session note\nA\n# Cast updates\n\n# Vault updates\nC\n").Problems);
    }

    [Fact]
    public void Lenient_FillsMissingSectionsFromTheFallback()
    {
        var partial = Instructions.ParseLenient("# Vault updates\nOnly places.", Instructions.Default, out var problems);

        Assert.Equal("Only places.", partial.VaultUpdates);
        Assert.Equal(Instructions.Default.SessionNote, partial.SessionNote);
        Assert.NotEmpty(problems);
    }

    [Fact]
    public void Compare_CountsLinesPerSection()
    {
        var before = new Instructions("a\nb\nc", "d", "x", "same");
        var after = new Instructions("a\n  b\nd\ne", "d", "x", "same");

        var changes = Instructions.Compare(before, after);

        Assert.Equal(new Instructions.SectionChange("Session note", 2, 1), changes[0]);
        Assert.False(changes[1].Changed);
        Assert.False(changes[2].Changed);
        Assert.False(changes[3].Changed);
    }

    // ── what the model gets ──────────────────────────────────────────────────

    [Fact]
    public void SystemPrompt_AddsTheNotASessionRules_Once()
    {
        var prompt = Prompts.BuildSystemPrompt(new Instructions("Write a limerick.", "- Keep the plans.", "x", "y"));
        Assert.StartsWith("Write a limerick.", prompt);
        Assert.Contains("NOT_A_SESSION:", prompt);
        Assert.Contains("CAMPAIGN_DISCUSSION:", prompt);
        Assert.Contains("RULES FOR A CAMPAIGN DISCUSSION:\n- Keep the plans.", prompt);

        // An older prompt file explains the markers itself.
        var legacy = Prompts.BuildSystemPrompt(new Instructions("Reply NOT_A_SESSION: why", "d", "x", "y"));
        Assert.Equal("Reply NOT_A_SESSION: why", legacy);
    }

    [Fact]
    public void Context_UsesTheGamesRules_AndKeepsTheFixedLayout()
    {
        Directory.CreateDirectory(Path.Combine(_root, "vault", "Places"));
        var vault = ObsidianVault.Open(new ObsidianConfig { VaultPath = Path.Combine(_root, "vault") });
        var instructions = new Instructions("s", "d", "- Cast rule.", "- Vault rule.");

        var context = vault.BuildContext(vault.ListNotes(), null, new DateTime(2026, 11, 22), out _, instructions);

        Assert.Contains("- Cast rule.", context);
        Assert.Contains("- Vault rule.", context);
        Assert.DoesNotContain("Lasting facts only", context);
        Assert.Contains(CastNote.UpdatesHeading, context);
        Assert.Contains(VaultUpdates.UpdatesHeading, context);
        Assert.Contains("### [[New name]] (new, Folder)", context);
        Assert.Contains("one of these folders: Places.", context);
    }

    // ── per game ─────────────────────────────────────────────────────────────

    [Fact]
    public void Game_WithoutItsOwnFile_UsesTheDefaultPromptFile()
    {
        File.WriteAllText(DefaultPrompt, "# Session note\nHouse style.\n");

        var loaded = Store().Load("Runeterra");

        Assert.False(loaded.Custom);
        Assert.Equal("House style.", loaded.Instructions.SessionNote);
        Assert.Equal(Instructions.Default.CastUpdates, loaded.Instructions.CastUpdates);
    }

    [Fact]
    public void Game_UsesItsOwnFile_AndFallsBackWhenItIsBroken()
    {
        Store().Save("Runeterra", Valid);

        var own = Store().Load("Runeterra");
        Assert.True(own.Custom);
        Assert.Equal("- Only places.", own.Instructions.VaultUpdates);
        Assert.Equal(Store().PathFor("Runeterra"), own.Path);

        // Other games are unaffected.
        Assert.False(Store().Load("Orc").Custom);

        File.WriteAllText(Store().PathFor("Runeterra"), "# Session note\nonly one section");
        Assert.False(Store().Load("Runeterra").Custom);
    }

    // ── discussions ──────────────────────────────────────────────────────────

    [Fact]
    public void AFileFromBeforeDiscussionRules_StillWorks_AndANewOneHasThem()
    {
        var old = Upload(Valid);
        Assert.True(old.Ok);
        Assert.Equal(Prompts.DefaultDiscussionNote, old.Instructions!.DiscussionNote);

        var current = Upload(Valid + "\n# Discussion note\n\n- Keep only the plans.\n");
        Assert.Equal("- Keep only the plans.", current.Instructions!.DiscussionNote);
        Assert.Contains("`# Discussion note` is empty.", Upload(Valid + "\n# Discussion note\n").Problems);

        var written = Instructions.Default.ToFile();
        Assert.True(written.IndexOf("# Session note", StringComparison.Ordinal) < written.IndexOf("# Discussion note", StringComparison.Ordinal));
        Assert.Equal(Instructions.Default, Instructions.Parse(written).Instructions);
    }

    [Fact]
    public void Game_WithAFileFromBeforeDiscussionRules_TakesThemFromTheDefaultFile()
    {
        File.WriteAllText(DefaultPrompt, "# Discussion note\nHouse rule for talks.\n");
        Store().Save("Runeterra", Valid);

        var loaded = Store().Load("Runeterra");

        Assert.True(loaded.Custom);
        Assert.Equal("- Only places.", loaded.Instructions.VaultUpdates);
        Assert.Equal("House rule for talks.", loaded.Instructions.DiscussionNote);
        Assert.Contains("House rule for talks.", Prompts.BuildSystemPrompt(loaded.Instructions));
    }

    [Fact]
    public void SaveAndReset_BackUpWhatTheyReplace()
    {
        var store = Store(new DateTime(2026, 11, 23, 20, 0, 0));
        Assert.Null(store.Save("Runeterra", "first"));

        var backup = store.Save("Runeterra", "second");
        Assert.Equal("first", File.ReadAllText(backup!));
        Assert.Equal("second", store.ReadOwn("Runeterra"));

        var resetBackup = store.Reset("Runeterra");
        Assert.NotEqual(backup, resetBackup);     // same second: a new name, not an overwrite
        Assert.Equal("second", File.ReadAllText(resetBackup!));
        Assert.Null(store.ReadOwn("Runeterra"));
        Assert.Null(store.Reset("Runeterra"));
    }
}
