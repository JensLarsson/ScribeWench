using ScribeWench.Core.Models;

namespace ScribeWench.Tests;

public class SummaryParserTests
{
    private const string SampleMarkdown = """
        ## What Happened
        The party ventured into the dungeon and fought a goblin ambush.

        ## Key Events
        - The party entered the dungeon
        - Goblins ambushed the party at the bridge
        - Thorin took 12 damage from a crossbow bolt

        ## NPCs Encountered
        - **Grak**: Goblin leader who fled before being captured

        ## Player Actions & Decisions
        - **Thorin** (Jens): Charged the goblin leader
        - **Lyra** (Sara): Cast fireball, destroying the bridge

        ## Cliffhanger
        The bridge is destroyed and the party is trapped on the wrong side of the chasm.

        ## Loot & Mechanical Notes
        - 50 gold pieces
        - Thorin levelled up to level 3
        """;

    [Fact]
    public void Parse_ExtractsKeyEvents()
    {
        var result = SummaryParser.Parse(SampleMarkdown);

        Assert.Equal(3, result.KeyEvents.Length);
        Assert.Contains("The party entered the dungeon", result.KeyEvents);
        Assert.Contains("Thorin took 12 damage from a crossbow bolt", result.KeyEvents);
    }

    [Fact]
    public void Parse_ExtractsNpcs()
    {
        var result = SummaryParser.Parse(SampleMarkdown);

        Assert.Single(result.NpcsIntroduced);
        Assert.Contains("Grak", result.NpcsIntroduced[0]);
    }

    [Fact]
    public void Parse_ExtractsPlayerActions()
    {
        var result = SummaryParser.Parse(SampleMarkdown);

        Assert.Equal(2, result.PlayerActions.Length);
        Assert.Contains(result.PlayerActions, a => a.Contains("Thorin"));
        Assert.Contains(result.PlayerActions, a => a.Contains("Lyra"));
    }

    [Fact]
    public void Parse_ExtractsCliffhanger()
    {
        var result = SummaryParser.Parse(SampleMarkdown);

        Assert.Contains("bridge is destroyed", result.Cliffhanger);
    }

    [Fact]
    public void Parse_PreservesFullMarkdown()
    {
        var result = SummaryParser.Parse(SampleMarkdown);

        Assert.Equal(SampleMarkdown, result.Markdown);
    }

    [Fact]
    public void Parse_EmptyMarkdown_ReturnsEmptyCollections()
    {
        var result = SummaryParser.Parse(string.Empty);

        Assert.Empty(result.KeyEvents);
        Assert.Empty(result.NpcsIntroduced);
        Assert.Empty(result.PlayerActions);
        Assert.Empty(result.Cliffhanger);
    }

    [Fact]
    public void Parse_MissingSections_DoesNotThrow()
    {
        var partial = "## What Happened\nSome stuff happened.\n\n## Key Events\n- One thing";
        var result = SummaryParser.Parse(partial);

        Assert.Single(result.KeyEvents);
        Assert.Empty(result.NpcsIntroduced);
        Assert.Empty(result.Cliffhanger);
    }

    [Theory]
    [InlineData("NOT_A_SESSION: A soundcheck.", RecordingKind.Neither, "A soundcheck.", "**Not a game session:** A soundcheck.")]
    [InlineData("NOT_A_SESSION: A soundcheck.\n\n## Vault Updates\n### [[Vol]]\n- Sings.\n", RecordingKind.Neither, "A soundcheck.", "**Not a game session:** A soundcheck.")]
    [InlineData("CAMPAIGN_DISCUSSION: Planning the next arc.", RecordingKind.Discussion, "Planning the next arc.", "**Campaign discussion, not a game session:** Planning the next arc.")]
    [InlineData("  campaign_discussion: Levelling choices.\n", RecordingKind.Discussion, "Levelling choices.", "**Campaign discussion, not a game session:** Levelling choices.")]
    public void NoSessionMarkers_GiveNoSessionNote(string reply, RecordingKind kind, string reason, string shown)
    {
        var result = SummaryParser.Parse(reply);

        Assert.True(result.NotASession);
        Assert.Equal(kind, result.Kind);
        Assert.Equal(reason, result.Reason);
        Assert.Equal("", result.Markdown);
        Assert.Equal(shown, SessionDocuments.Body(result));
    }

    [Fact]
    public void ADiscussion_KeepsWhatFollowsItsMarker()
    {
        var result = SummaryParser.Parse(
            "CAMPAIGN_DISCUSSION: Planning the heist.\n\n## The plan\n- Go in by the sewers.\n\n## Vault Updates\n### [[Vol]]\n- Owns the vault.\n");

        Assert.Equal(RecordingKind.Discussion, result.Kind);
        Assert.Equal("Planning the heist.", result.Reason);
        Assert.StartsWith("## The plan\n- Go in by the sewers.", result.Markdown);
        Assert.Contains("## Vault Updates", result.Markdown);       // split off later, as for a session
    }

    [Fact]
    public void ASession_IsNotFlagged()
    {
        var result = SummaryParser.Parse("## What Happened\nThey fought.\n\n## Cliffhanger\nA door opened.\n\n## Cast Updates\n- None\n");

        Assert.False(result.NotASession);
        Assert.Equal("A door opened.", result.Cliffhanger);
    }

    [Fact]
    public void DefaultPrompt_IsWhatANewPromptFileHolds()
    {
        var path = Path.Combine(Path.GetTempPath(), "scribewench-tests", Guid.NewGuid().ToString("N"), "summary-prompt.md");
        try
        {
            InstructionsStore.EnsureFile(path);
            var parsed = Instructions.Parse(File.ReadAllText(path));
            Assert.Empty(parsed.Problems);
            Assert.DoesNotContain(Instructions.Compare(Instructions.Default, parsed.Instructions!), c => c.Changed);
        }
        finally
        {
            try { Directory.Delete(Path.GetDirectoryName(path)!, recursive: true); } catch { }
        }
    }
}
