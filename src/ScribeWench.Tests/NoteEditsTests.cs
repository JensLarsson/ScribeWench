using ScribeWench.Core.Models;

namespace ScribeWench.Tests;

public sealed class NoteEditsTests : IDisposable
{
    private readonly string _base = Path.Combine(Path.GetTempPath(), "scribewench-tests", Guid.NewGuid().ToString("N"));
    private string Root => Path.Combine(_base, "vault");
    private string Backups => Path.Combine(_base, "backups");
    private static readonly DateTime Now = new(2026, 11, 22, 18, 30, 0);

    public NoteEditsTests() => Directory.CreateDirectory(Root);

    public void Dispose()
    {
        try { Directory.Delete(_base, recursive: true); } catch { }
    }

    private string WriteNote(string relativePath, string content)
    {
        var path = Path.Combine(Root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private ObsidianVault Vault() => ObsidianVault.Open(new ObsidianConfig { VaultPath = Root });

    private NoteEdits.Outcome Apply(params NoteEdits.Edit[] edits) =>
        NoteEdits.Apply(Vault(), new NoteEdits.Plan("Done.", edits), Backups, Now);

    private const string Reply = """
        ## Reply
        Orfelt made the portal; that he opened it was only a guess. Fixed in two notes.

        ## Edits
        ### NPCs/Orfelt.md
        <<<<<<< FIND
        - [[2026-11-22 Runeterra]]: Opened the Bandle portal beneath the castle.
        =======
        - [[2026-11-22 Runeterra]]: Created the Bandle portal beneath the castle.
        >>>>>>> END
        ### Sessions/2026-11-22 Runeterra.md
        <<<<<<< FIND
        Orfelt had.
        =======
        Orfelt had made it.
        >>>>>>> END
        ### Items/Portal stone.md (new)
        <<<<<<< FIND
        =======
        A stone made by [[Orfelt]].

        ## Noted by ScribeWench
        >>>>>>> END
        """;

    [Fact]
    public void Parse_ReadsReplyAndEveryBlock()
    {
        var plan = NoteEdits.Parse(Reply);

        Assert.Equal("Orfelt made the portal; that he opened it was only a guess. Fixed in two notes.", plan.Reply);
        Assert.Equal(3, plan.Edits.Count);
        Assert.Equal("NPCs/Orfelt.md", plan.Edits[0].Path);
        Assert.Equal("Orfelt had.", plan.Edits[1].Find);
        Assert.True(plan.Edits[2].IsNew);
        Assert.Equal("Items/Portal stone.md", plan.Edits[2].Path);
        // A heading inside a block is content, not a new section.
        Assert.Equal("A stone made by [[Orfelt]].\n\n## Noted by ScribeWench", plan.Edits[2].Replace);
    }

    [Fact]
    public void Parse_None_HasNoEdits()
    {
        var plan = NoteEdits.Parse("## Reply\nThat isn't about the notes.\n\n## Edits\nNone\n");
        Assert.Equal("That isn't about the notes.", plan.Reply);
        Assert.Empty(plan.Edits);
    }

    [Fact]
    public void Apply_ReplacesExactlyTheText_AndBacksUpTheOriginal()
    {
        var orfelt = WriteNote("NPCs/Orfelt.md", "A gnome.\n\n## Noted by ScribeWench\n\n- [[2026-11-22 Runeterra]]: Opened the Bandle portal beneath the castle.\n");
        WriteNote("Sessions/2026-11-22 Runeterra.md", "Antrew never opened it. Orfelt had.\n");
        WriteNote("Items/Chalice.md", "Red's.");

        var outcome = NoteEdits.Apply(Vault(), NoteEdits.Parse(Reply), Backups, Now);

        Assert.Equal("A gnome.\n\n## Noted by ScribeWench\n\n- [[2026-11-22 Runeterra]]: Created the Bandle portal beneath the castle.\n", File.ReadAllText(orfelt));
        Assert.Equal("Antrew never opened it. Orfelt had made it.\n", File.ReadAllText(Path.Combine(Root, "Sessions", "2026-11-22 Runeterra.md")));
        Assert.Equal("A stone made by [[Orfelt]].\n\n## Noted by ScribeWench\n", File.ReadAllText(Path.Combine(Root, "Items", "Portal stone.md")));
        Assert.Equal(["Orfelt", "2026-11-22 Runeterra", "Portal stone"], outcome.ChangedNotes);
        Assert.Empty(outcome.Failed);

        Assert.Equal(Path.Combine(Backups, "20261122-183000"), outcome.BackupFolder);
        Assert.Contains("Opened the Bandle portal", File.ReadAllText(Path.Combine(outcome.BackupFolder!, "NPCs", "Orfelt.md")));
    }

    [Fact]
    public void Apply_TextNotFoundOrRepeated_LeavesTheNoteAlone()
    {
        var vol = WriteNote("NPCs/Vol.md", "A lord. A lord.\n");

        var outcome = Apply(
            new NoteEdits.Edit("NPCs/Vol.md", false, "A lord.", "A boy."),
            new NoteEdits.Edit("NPCs/Vol.md", false, "A king.", "A boy."));

        Assert.Equal("A lord. A lord.\n", File.ReadAllText(vol));
        Assert.Empty(outcome.Applied);
        Assert.Equal(2, outcome.Failed.Count);
        Assert.Null(outcome.BackupFolder);
    }

    [Fact]
    public void Apply_KeepsWindowsLineEndings()
    {
        var vol = WriteNote("NPCs/Vol.md", "Line one.\r\nLine two.\r\n");

        Apply(new NoteEdits.Edit("NPCs/Vol.md", false, "Line one.\nLine two.", "Line one.\nLine 2."));

        Assert.Equal("Line one.\r\nLine 2.\r\n", File.ReadAllText(vol));
    }

    [Theory]
    [InlineData("../outside.md")]
    [InlineData("Transcripts/Runeterra_2026-11-22_transcript.md")]
    [InlineData(".obsidian/workspace.md")]
    [InlineData("NPCs/Vol.txt")]
    public void Apply_RefusesPathsOutsideTheNotes(string path)
    {
        WriteNote("Transcripts/Runeterra_2026-11-22_transcript.md", "words");

        var outcome = Apply(new NoteEdits.Edit(path, true, "", "New text."));

        Assert.Empty(outcome.Applied);
        Assert.Single(outcome.Failed);
        Assert.False(File.Exists(Path.Combine(_base, "outside.md")));
        Assert.Equal("words", File.ReadAllText(Path.Combine(Root, "Transcripts", "Runeterra_2026-11-22_transcript.md")));
    }

    [Fact]
    public void Apply_NewNoteInAFolderThatDoesntExist_IsRefused()
    {
        WriteNote("NPCs/Vol.md", "Lord.");

        var outcome = Apply(new NoteEdits.Edit("Spirits/The Sage.md", true, "", "A presence."));

        Assert.Empty(outcome.Applied);
        Assert.False(Directory.Exists(Path.Combine(Root, "Spirits")));
    }

    [Fact]
    public void Message_HoldsEveryNoteButTranscripts_AndTheInstruction()
    {
        WriteNote("Cast.md", "- **Ralkern**: DM");
        WriteNote("NPCs/Orfelt.md", "---\naliases: [Orf]\n---\nA gnome.");
        WriteNote("Transcripts/Runeterra_2026-11-22_transcript.md", "secret words");

        var message = NoteEdits.BuildMessage(Vault(), "Runeterra", "Orfelt made the portal.", "Jens", 400_000);

        Assert.Contains("<note path=\"Cast.md\">\n- **Ralkern**: DM\n</note>", message.ReplaceLineEndings("\n"));
        Assert.Contains("<note path=\"NPCs/Orfelt.md\">\n---\naliases: [Orf]\n---\nA gnome.\n</note>", message.ReplaceLineEndings("\n"));
        Assert.DoesNotContain("secret words", message);
        Assert.EndsWith("INSTRUCTION (from Jens):\nOrfelt made the portal.", message.ReplaceLineEndings("\n").TrimEnd());
    }

    [Fact]
    public void Discord_ShowsChangedLines_WithinTheLimit()
    {
        var outcome = new NoteEdits.Outcome("Fixed.",
            [new NoteEdits.Change("Orfelt", ["- Opened the portal."], ["- Created the portal."])], [], null);

        var text = NoteEdits.FormatForDiscord(outcome);

        Assert.Contains("Changed: **Orfelt**", text);
        Assert.Contains("```diff\n# Orfelt\n- - Opened the portal.\n+ - Created the portal.\n```", text);

        var many = new NoteEdits.Outcome("Fixed.",
            Enumerable.Range(0, 200).Select(i => new NoteEdits.Change($"Note {i}", [new string('x', 200)], [new string('y', 200)])).ToList(), [], null);
        Assert.True(NoteEdits.FormatForDiscord(many).Length <= 1900);
    }
}
