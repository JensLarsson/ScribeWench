using ScribeWench.Tests.Support;
using ScribeWench.Core.Models;

namespace ScribeWench.Tests;

public sealed class WhereaboutsTests : IDisposable
{
    private const string Label = "[[2026-11-22 Runeterra]]";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "scribewench-tests", Guid.NewGuid().ToString("N"));

    public WhereaboutsTests() => Directory.CreateDirectory(_root);

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

    private static VaultUpdates.Update Seen(string name, params string[] places) =>
        new(name, false, null, null, []) { Whereabouts = places };

    [Fact]
    public void Split_ReadsWhereabouts_AndSkipsLabelLines()
    {
        const string reply = """
            ## Vault Updates
            ### [[Via]]
            - **Last whereabouts**
            - Finds the [[Bandle Woods]] restful.
            - **Vol** is her brother.
            Whereabouts: Last seen camped in the [[Bandle Woods]] with [[Dandelion]].
            - **Whereabouts:** Claimed to be at the lake, according to [[Jom and Terry]].
            """;

        var update = VaultUpdates.Split(reply).Updates.Single();

        Assert.Equal(["Finds the [[Bandle Woods]] restful.", "**Vol** is her brother."], update.Facts);
        Assert.Equal(
            ["Last seen camped in the [[Bandle Woods]] with [[Dandelion]].", "Claimed to be at the lake, according to [[Jom and Terry]]."],
            update.Whereabouts);
    }

    [Fact]
    public void Split_OnlyWhereabouts_IsStillAnUpdate()
    {
        var update = VaultUpdates.Split("## Vault Updates\n### [[Vol]]\nWhereabouts: In the portal.\n").Updates.Single();

        Assert.Empty(update.Facts);
        Assert.Equal(["In the portal."], update.Whereabouts);
    }

    [Fact]
    public void Whereabouts_GoAtTheEnd_AndTheNextReportReplacesThem()
    {
        var via = WriteNote("NPCs/Via.md", "Daughter.\n\n## From the sessions\n\n- [[2026-11-15 Runeterra]]: Fact.\n");

        VaultUpdates.Apply(Vault(), [Seen("Via", "At the castle.")], "[[2026-11-15 Runeterra]]", TestNotes.Backups);
        var result = VaultUpdates.Apply(Vault(),
            [Seen("Via", "In the [[Bandle Woods]].", "Claimed to be at the lake, according to [[Vol]].")], Label, TestNotes.Backups);

        Assert.Equal(["Via"], result.Updated);
        Assert.Equal(
            "Daughter.\n\n## From the sessions\n\n- [[2026-11-15 Runeterra]]: Fact.\n\n## Last whereabouts\n\n" +
            "- [[2026-11-22 Runeterra]]: In the [[Bandle Woods]].\n" +
            "- [[2026-11-22 Runeterra]]: Claimed to be at the lake, according to [[Vol]].\n",
            Read(via));
    }

    [Fact]
    public void Whereabouts_Unchanged_LeaveTheNoteAlone_AndKeepTheSectionAfter()
    {
        var vol = WriteNote("NPCs/Vol.md",
            "Lord.\n\n## Last whereabouts\n\n- [[2026-11-15 Runeterra]]: In the portal.\n\n## Dandelion's view\n\nA coward.\n");

        var result = VaultUpdates.Apply(Vault(), [Seen("Vol", "In the portal.")], Label, TestNotes.Backups);

        Assert.Empty(result.Updated);
        Assert.Contains("[[2026-11-15 Runeterra]]: In the portal.", Read(vol));

        VaultUpdates.Apply(Vault(), [Seen("Vol", "In the woods.")], Label, TestNotes.Backups);
        Assert.Equal(
            "Lord.\n\n## Last whereabouts\n\n- [[2026-11-22 Runeterra]]: In the woods.\n\n## Dandelion's view\n\nA coward.\n",
            Read(vol));
    }

    [Fact]
    public void FirstFacts_GoInFrontOfLastWhereabouts()
    {
        var vol = WriteNote("NPCs/Vol.md", "Lord.\n\n## Last whereabouts\n\n- [[2026-11-15 Runeterra]]: In the portal.\n");

        VaultUpdates.Apply(Vault(), [new VaultUpdates.Update("Vol", false, null, null, ["Opened the portal."])], Label, TestNotes.Backups);

        Assert.Equal(
            "Lord.\n\n## From the sessions\n\n- [[2026-11-22 Runeterra]]: Opened the portal.\n\n" +
            "## Last whereabouts\n\n- [[2026-11-15 Runeterra]]: In the portal.\n",
            Read(vol));
    }

    [Fact]
    public void NewNote_GetsItsWhereabouts()
    {
        WriteNote("NPCs/Vol.md", "Lord.");
        var update = new VaultUpdates.Update("Terry", true, "NPCs", "A scout.", ["Has a badge."]) { Whereabouts = ["Swept down a Fae road."] };

        VaultUpdates.Apply(Vault(), [update], Label, TestNotes.Backups);

        Assert.EndsWith("## Last whereabouts\n\n- [[2026-11-22 Runeterra]]: Swept down a Fae road.\n",
            Read(Path.Combine(_root, "NPCs", "Terry.md")));
    }

    [Fact]
    public void Instructions_ExplainWhereabouts()
    {
        var text = VaultUpdates.BuildInstructions(["NPCs"]);

        Assert.Contains("Whereabouts: [where they are now", text);
        Assert.Contains("\"Last whereabouts\" section", text);
    }
}
