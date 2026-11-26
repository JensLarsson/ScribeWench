using ScribeWench.Tests.Support;
using ScribeWench.Core.Models;

namespace ScribeWench.Tests;

public sealed class VaultUpdatesTests : IDisposable
{
    private const string Label = "[[2026-11-22 Runeterra]]";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "scribewench-tests", Guid.NewGuid().ToString("N"));

    public VaultUpdatesTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private string WriteNote(string relativePath, string content)
    {
        var path = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private ObsidianVault Vault() => ObsidianVault.Open(new ObsidianConfig { VaultPath = _root });

    private static string Read(string path) => File.ReadAllText(path).ReplaceLineEndings("\n");

    private const string Reply = """
        ## What Happened
        Red talked to the Sage.

        ## Cliffhanger
        Darkness.

        ## Vault Updates
        ### [[Vol]]
        - Opened a passage with [[Dandelion]]'s blood.
        ### [[Sage]] (new, NPCs)
        A presence that answers [[Sly Red]] in the dark.
        - Aided Red's Arcana check on the missing portal.
        ### Jom and Terry (new)
        - Swept away by a flood in the [[Bandle Woods]].

        ## Cast Updates
        - None
        """;

    [Fact]
    public void Split_RemovesTheSection_AndReadsEachNote()
    {
        var (markdown, updates) = VaultUpdates.Split(Reply);

        Assert.DoesNotContain("Vault Updates", markdown);
        Assert.Contains("## Cliffhanger\nDarkness.", markdown.ReplaceLineEndings("\n"));
        Assert.Contains("## Cast Updates", markdown);

        Assert.Equal(3, updates.Count);
        Assert.Equal("Vol", updates[0].Name);
        Assert.False(updates[0].IsNew);
        Assert.Null(updates[0].Description);
        Assert.Equal(["Opened a passage with [[Dandelion]]'s blood."], updates[0].Facts);
        Assert.Equal("Sage", updates[1].Name);
        Assert.True(updates[1].IsNew);
        Assert.Equal("NPCs", updates[1].Folder);
        Assert.Equal("A presence that answers [[Sly Red]] in the dark.", updates[1].Description);
        Assert.Equal("Jom and Terry", updates[2].Name);
        Assert.Null(updates[2].Folder);
    }

    [Fact]
    public void Split_None_GivesNoUpdates()
    {
        var (_, updates) = VaultUpdates.Split("## What Happened\nStuff.\n\n## Vault Updates\nNone\n");
        Assert.Empty(updates);
    }

    [Fact]
    public void Apply_AppendsToExistingNotes_UnderTheirOwnHeading_LeavingTheRestAlone()
    {
        var vol = WriteNote("NPCs/Vol.md", "The young lord.\n\n- Carries powerful magic.\n");
        var (_, updates) = VaultUpdates.Split(Reply);

        var result = VaultUpdates.Apply(Vault(), updates, Label, TestNotes.Backups);

        Assert.Equal(
            "The young lord.\n\n- Carries powerful magic.\n\n## From the sessions\n\n- [[2026-11-22 Runeterra]]: Opened a passage with [[Dandelion]]'s blood.\n",
            Read(vol));
        Assert.Contains("Vol", result.Updated);
    }

    [Fact]
    public void Apply_AddsToTheEndOfTheNotedSection_WhenAHeadingFollowsIt()
    {
        var vol = WriteNote("NPCs/Vol.md", "Lord.\n\n## From the sessions\n\n- [[2026-11-15 Runeterra]]: Older fact.\n\n## Dandelion's view\n\nA coward.\n");
        VaultUpdates.Apply(Vault(), [new VaultUpdates.Update("Vol", false, null, null, ["New fact."])], Label, TestNotes.Backups);

        Assert.Equal(
            "Lord.\n\n## From the sessions\n\n- [[2026-11-15 Runeterra]]: Older fact.\n- [[2026-11-22 Runeterra]]: New fact.\n\n## Dandelion's view\n\nA coward.\n",
            Read(vol));
    }

    [Fact]
    public void Apply_CreatesNewNotes_InAnExistingFolderOnly()
    {
        WriteNote("NPCs/Vol.md", "Lord.");
        var (_, updates) = VaultUpdates.Split(Reply);

        var result = VaultUpdates.Apply(Vault(), updates, Label, TestNotes.Backups);

        Assert.Equal(["Sage", "Jom and Terry"], result.Created);
        Assert.Equal(
            "A presence that answers [[Sly Red]] in the dark.\n\nFirst appeared in [[2026-11-22 Runeterra]].\n\n## From the sessions\n\n- [[2026-11-22 Runeterra]]: Aided Red's Arcana check on the missing portal.\n",
            Read(Path.Combine(_root, "NPCs", "Sage.md")));
        // No folder given: the game's own folder.
        Assert.True(File.Exists(Path.Combine(_root, "Jom and Terry.md")));
    }

    [Fact]
    public void Apply_UnknownFolder_GoesInTheGameFolder()
    {
        VaultUpdates.Apply(Vault(), [new VaultUpdates.Update("Sage", true, "Spirits", "A presence.", [])], Label, TestNotes.Backups);

        Assert.True(File.Exists(Path.Combine(_root, "Sage.md")));
        Assert.False(Directory.Exists(Path.Combine(_root, "Spirits")));
    }

    [Fact]
    public void Apply_FindsNotesByAlias_AndSkipsFactsAlreadyThere()
    {
        var antrew = WriteNote("NPCs/Antrew Eulerlakt.md", "---\naliases: [Antrew]\n---\nOf House Eulerlakt.\n\n- Owned the vault.\n");

        var result = VaultUpdates.Apply(Vault(), [new VaultUpdates.Update("Antrew", false, null, null, ["Owned the vault."])], Label, TestNotes.Backups);

        Assert.Empty(result.Updated);
        Assert.Empty(result.Created);
        Assert.Equal("---\naliases: [Antrew]\n---\nOf House Eulerlakt.\n\n- Owned the vault.\n", Read(antrew));
        Assert.False(File.Exists(Path.Combine(_root, "Antrew.md")));
    }

    [Fact]
    public void Apply_NeverTouchesSessionNotesOrTheCast()
    {
        var session = WriteNote("Sessions/2026-11-15 Runeterra.md", "Last time.");
        var cast = WriteNote("Cast.md", "# Cast\n- **Ralkern**: DM\n");

        var result = VaultUpdates.Apply(Vault(),
        [
            new VaultUpdates.Update("2026-11-15 Runeterra", false, null, null, ["Rewritten."]),
            new VaultUpdates.Update("Cast", true, null, "Cast.", ["Someone."]),
        ], Label, TestNotes.Backups);

        Assert.Empty(result.Created);
        Assert.Empty(result.Updated);
        Assert.Equal("Last time.", Read(session));
        Assert.Equal("# Cast\n- **Ralkern**: DM\n", Read(cast));
    }

    [Fact]
    public void Apply_KeepsWindowsLineEndings()
    {
        var vol = WriteNote("NPCs/Vol.md", "Lord.\r\n");
        VaultUpdates.Apply(Vault(), [new VaultUpdates.Update("Vol", false, null, null, ["Fact."])], Label, TestNotes.Backups);

        Assert.Equal("Lord.\r\n\r\n## From the sessions\r\n\r\n- [[2026-11-22 Runeterra]]: Fact.\r\n", File.ReadAllText(vol));
    }

    [Fact]
    public void Context_ListsTheFoldersNewNotesCanGoIn()
    {
        WriteNote("NPCs/Vol.md", "Lord.");
        WriteNote("Places/Bilgewater.md", "Port.");
        WriteNote("Sessions/2026-11-15 Runeterra.md", "Last time.");
        var vault = Vault();

        var context = vault.BuildContext(vault.ListNotes(), null, new DateTime(2026, 11, 22), out _);

        Assert.Contains("## Vault Updates", context);
        Assert.Contains("one of these folders: NPCs, Places.", context);
    }

    [Fact]
    public void Context_WithoutUpdateNotes_HasNoInstructions()
    {
        var vault = ObsidianVault.Open(new ObsidianConfig { VaultPath = _root, UpdateNotes = false });
        var context = vault.BuildContext(vault.ListNotes(), null, new DateTime(2026, 11, 22), out _);
        Assert.DoesNotContain("Vault Updates", context);
    }
}
