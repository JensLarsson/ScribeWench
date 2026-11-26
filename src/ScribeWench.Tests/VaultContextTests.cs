using ScribeWench.Core.Models;

namespace ScribeWench.Tests;

public sealed class VaultContextTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "scribewench-tests", Guid.NewGuid().ToString("N"));
    private static readonly DateTime Session = new(2026, 11, 15, 19, 0, 0);

    public VaultContextTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private void WriteNote(string relativePath, string content)
    {
        var path = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private (string Context, ObsidianVault.ContextStats Stats) Build(ObsidianConfig? config = null)
    {
        config ??= new ObsidianConfig();
        config.VaultPath = _root;
        var vault = ObsidianVault.Open(config);
        var context = vault.BuildContext(vault.ListNotes(), vault.FindPreviousSessionNote(Session), Session, out var stats);
        return (context, stats);
    }

    [Fact]
    public void SendsEveryNoteInFull_WithoutFrontmatter_SessionsOldestFirst()
    {
        WriteNote("NPCs/Sesshua.md", "---\naliases: [The Serpent]\n---\n# Sesshua\n\nSnakefolk priestess.");
        WriteNote("Sessions/2026-11-08 Orc.md", "---\ndate: 2026-11-08\n---\nThey met the Magi.");
        WriteNote("Sessions/2026-11-01 Orc.md", "They left the Tiding.");
        WriteNote("Transcripts/Orc_2026-11-08_19-00_transcript.md", "[00:00:00] **Erik**: secret words");

        var (context, stats) = Build();

        Assert.Contains("<note name=\"Sesshua\" folder=\"NPCs\" aliases=\"The Serpent\">\n# Sesshua\n\nSnakefolk priestess.\n</note>", context.ReplaceLineEndings("\n"));
        Assert.Contains("<note name=\"2026-11-08 Orc\" folder=\"Sessions\" previous-session=\"true\">", context);
        Assert.True(context.IndexOf("They left the Tiding.", StringComparison.Ordinal) < context.IndexOf("They met the Magi.", StringComparison.Ordinal));
        Assert.DoesNotContain("date: 2026-11-08", context);
        Assert.DoesNotContain("secret words", context);
        Assert.Equal(3, stats.NotesInFull);
        Assert.Equal(0, stats.NotesByNameOnly);
    }

    [Fact]
    public void ThisSessionsOwnEarlierNote_IsNotEchoedBack()
    {
        WriteNote("Sessions/2026-11-15 Orc.md", "An earlier run of this same summary.");

        var (context, stats) = Build();

        Assert.Contains("<note name=\"2026-11-15 Orc\" folder=\"Sessions\" />", context);
        Assert.DoesNotContain("An earlier run", context);
        Assert.Equal(1, stats.NotesByNameOnly);
    }

    [Fact]
    public void OverTheLimit_OldestSessionsGoFirst_PreviousSessionAndNamesStay()
    {
        WriteNote("NPCs/Grak.md", new string('g', 100));
        WriteNote("Sessions/2026-10-01 Orc.md", new string('a', 1000));
        WriteNote("Sessions/2026-10-08 Orc.md", new string('b', 1000));
        WriteNote("Sessions/2026-11-08 Orc.md", new string('c', 1000));

        var (context, stats) = Build(new ObsidianConfig { MaxNoteCharacters = 1500 });

        Assert.DoesNotContain("aaaa", context);
        Assert.DoesNotContain("bbbb", context);
        Assert.Contains("cccc", context);
        Assert.Contains("gggg", context);
        Assert.Contains("<note name=\"2026-10-01 Orc\" folder=\"Sessions\" />", context);
        Assert.Equal((2, 2, 1100), (stats.NotesInFull, stats.NotesByNameOnly, stats.Characters));
    }

    [Fact]
    public void SendFullNotesOff_SendsNamesAndThePreviousSessionOnly()
    {
        WriteNote("NPCs/Sesshua.md", "Snakefolk priestess.");
        WriteNote("Sessions/2026-11-01 Orc.md", "They left the Tiding.");
        WriteNote("Sessions/2026-11-08 Orc.md", "They met the Magi.");

        var (context, _) = Build(new ObsidianConfig { SendFullNotes = false });

        Assert.Contains("- Sesshua (NPCs)", context);
        Assert.DoesNotContain("Snakefolk priestess.", context);
        Assert.DoesNotContain("They left the Tiding.", context);
        Assert.Contains("They met the Magi.", context);
    }

    [Fact]
    public void ALowerLimitForOneRequest_CutsNoteText_ButNeverRaisesTheConfiguredLimit()
    {
        WriteNote("NPCs/Grak.md", new string('g', 100));
        WriteNote("Sessions/2026-10-01 Orc.md", new string('a', 1000));
        WriteNote("Sessions/2026-11-08 Orc.md", new string('c', 1000));

        var config = new ObsidianConfig { VaultPath = _root, MaxNoteCharacters = 1500 };
        var vault = ObsidianVault.Open(config);
        var notes = vault.ListNotes();
        var previous = vault.FindPreviousSessionNote(Session);

        var small = vault.BuildContext(notes, previous, Session, out var smallStats, maxNoteCharacters: 150);
        Assert.Contains("gggg", small);
        Assert.DoesNotContain("cccc", small);
        Assert.Contains("<note name=\"2026-11-08 Orc\" folder=\"Sessions\" previous-session=\"true\" />", small);
        Assert.Equal(100, smallStats.Characters);

        var none = vault.BuildContext(notes, previous, Session, out _, maxNoteCharacters: 0);
        Assert.DoesNotContain("gggg", none);
        Assert.Contains("<note name=\"Grak\" folder=\"NPCs\" />", none);
        Assert.Contains("## Cast Updates", none);        // the rules for the reply are never cut
        Assert.Contains("## Vault Updates", none);

        vault.BuildContext(notes, previous, Session, out var hugeStats, maxNoteCharacters: 1_000_000);
        Assert.Equal(1100, hugeStats.Characters);        // still held to the configured 1,500
    }

    [Fact]
    public void PartContext_HasTheCastAndTheNoteNames_NotSessionsOrNoteText()
    {
        WriteNote("Cast.md", "# Cast\n\n- **Erik**: DM\n- **Anna**: plays Thorin");
        WriteNote("NPCs/Sesshua.md", "---\naliases: [The Serpent]\n---\nSnakefolk priestess.");
        WriteNote("Places/Bandle City.md", "A yordle city.");
        WriteNote("Sessions/2026-11-08 Orc.md", "They met the Magi.");

        var vault = ObsidianVault.Open(new ObsidianConfig { VaultPath = _root });
        var text = vault.BuildPartContext(vault.ListNotes());

        Assert.Contains("- **Anna**: plays Thorin", text);
        Assert.Contains("Sesshua (also: The Serpent); Bandle City", text);
        Assert.DoesNotContain("2026-11-08", text);
        Assert.DoesNotContain("Snakefolk", text);

        var cut = vault.BuildPartContext(vault.ListNotes(), maxCharacters: text.IndexOf("Bandle", StringComparison.Ordinal) + 3);
        Assert.Contains("Sesshua", cut);
        Assert.DoesNotContain("Bandle", cut);
    }

    [Fact]
    public void SummaryContext_CanBeCutToAGivenLength_KeepingNamesAndRules()
    {
        WriteNote("Game/Cast.md", "- **Erik**: DM");
        WriteNote("Game/NPCs/Grak.md", new string('g', 3000));
        WriteNote("Game/Sessions/2026-10-01 Orc.md", new string('a', 3000));
        WriteNote("Game/Sessions/2026-11-08 Orc.md", new string('c', 3000));

        var config = new AppConfig();
        config.Obsidian.VaultPath = _root;
        config.Ai.PromptFile = Path.Combine(_root, "prompt-outside-the-game.md");

        var built = Support.TestNotes.Summarizer(config, _root).Prepare(new SessionDetails("Orc", Session, "Game", "x"));
        var full = built.Options.Context!;
        Assert.Contains("aaaa", full);
        Assert.NotNull(built.Options.ContextWithin);
        Assert.Contains("- **Erik**: DM", built.Options.PartContext);

        Assert.Equal(full, built.Options.ContextWithin!(full.Length));

        var shorter = built.Options.ContextWithin!(full.Length - 4000);
        Assert.True(shorter.Length <= full.Length - 4000);
        Assert.DoesNotContain("aaaa", shorter);                  // the oldest session goes first
        Assert.Contains("cccc", shorter);
        Assert.Contains("<note name=\"2026-10-01 Orc\"", shorter);
        Assert.Contains("## Vault Updates", shorter);

        var smallest = built.Options.ContextWithin!(10);         // can't be met: names and rules stay regardless
        Assert.DoesNotContain("gggg", smallest);
        Assert.Contains("<note name=\"Grak\"", smallest);
    }
}
