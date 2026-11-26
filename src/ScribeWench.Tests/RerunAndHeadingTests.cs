using ScribeWench.Tests.Support;
using ScribeWench.Core.Models;

namespace ScribeWench.Tests;

public sealed class RerunAndHeadingTests : IDisposable
{
    private const string Label = "[[2026-11-22 Runeterra]]";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "scribewench-tests", Guid.NewGuid().ToString("N"));
    private string Vault => Path.Combine(_root, "vault");

    public RerunAndHeadingTests() => Directory.CreateDirectory(Vault);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private string WriteNote(string relativePath, string content)
    {
        var path = Path.Combine(Vault, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private AppConfig Config() => new()
    {
        Ai = { PromptFile = Path.Combine(_root, "summary-prompt.md") },
        Obsidian = { VaultPath = Vault },
    };

    private static string Read(string path) => File.ReadAllText(path).ReplaceLineEndings("\n");

    // ── the old heading ──────────────────────────────────────────────────────

    [Fact]
    public void OldHeading_IsRenamed_InNotesAndCast_ButNotInComments()
    {
        var vol = WriteNote("NPCs/Vol.md", "Lord.\n\n## Noted by ScribeWench\n\n- [[2026-11-15 Runeterra]]: Fact.\n");
        var cast = WriteNote("Cast.md", "<!-- added under \"Noted by ScribeWench\" -->\n\n## Noted by ScribeWench\n\n- 2026-11-15: x\n");
        var plain = WriteNote("NPCs/Via.md", "Daughter.\n");

        var renamed = VaultUpdates.RenameLegacyHeadings(ObsidianVault.Open(Config().Obsidian));

        Assert.Equal(["Vol", "Cast"], renamed);
        Assert.Equal("Lord.\n\n## From the sessions\n\n- [[2026-11-15 Runeterra]]: Fact.\n", Read(vol));
        Assert.Equal("<!-- added under \"Noted by ScribeWench\" -->\n\n## From the sessions\n\n- 2026-11-15: x\n", Read(cast));
        Assert.Equal("Daughter.\n", Read(plain));
        Assert.Empty(VaultUpdates.RenameLegacyHeadings(ObsidianVault.Open(Config().Obsidian)));
    }

    [Fact]
    public void NewFacts_GoUnderTheOldSection_RenamedRatherThanASecondOne()
    {
        var vol = WriteNote("NPCs/Vol.md", "Lord.\n\n## Noted by ScribeWench\n\n- [[2026-11-15 Runeterra]]: Old.\n");

        VaultUpdates.Apply(ObsidianVault.Open(Config().Obsidian), [new VaultUpdates.Update("Vol", false, null, null, ["New."])], Label, TestNotes.Backups);

        Assert.Equal("Lord.\n\n## From the sessions\n\n- [[2026-11-15 Runeterra]]: Old.\n- [[2026-11-22 Runeterra]]: New.\n", Read(vol));
    }

    [Fact]
    public void CastAppend_RenamesTheOldHeading()
    {
        var cast = WriteNote("Cast.md", "# Cast\n\n## Noted by ScribeWench\n\n- 2026-11-15: old\n");

        CastNote.Append(cast, ["new"], new DateTime(2026, 11, 22));

        Assert.Equal("# Cast\n\n## From the sessions\n\n- 2026-11-15: old\n- 2026-11-22: new\n", Read(cast));
    }

    // ── defaults ─────────────────────────────────────────────────────────────

    [Fact]
    public void Defaults_UseCharacterNamesOnly_AndTrackWhereabouts()
    {
        Assert.DoesNotContain("[Player]", Prompts.DefaultSessionNote);
        Assert.Contains("never write a player's real name or Discord name", Prompts.DefaultSessionNote);
        Assert.Contains("Last seen at [[Place]] with [[Character]]", Prompts.DefaultVaultUpdates);
        Assert.DoesNotContain("Noted by ScribeWench", Prompts.DefaultVaultUpdates);
        Assert.Contains("player characters included", Prompts.DefaultVaultUpdates);
    }

    [Fact]
    public void Whereabouts_StartWithACapital()
    {
        var update = VaultUpdates.Split("## Vault Updates\n### [[Vol]]\nWhereabouts: in the [[Bandle Woods]].\nWhereabouts: [[Bilgewater]], according to [[Via]].\n").Updates.Single();
        Assert.Equal(["In the [[Bandle Woods]].", "[[Bilgewater]], according to [[Via]]."], update.Whereabouts);
    }

    // ── re-runs ──────────────────────────────────────────────────────────────

    private static readonly DateTime Session = new(2026, 11, 22, 14, 31, 0);

    [Fact]
    public void Rerun_SendsTheCurrentNote_AndKeepsItOutOfTheLinks()
    {
        var current = WriteNote("Runeterra/Sessions/2026-11-22 Runeterra.md",
            "---\ndate: 2026-11-22\n---\n\n## What Happened\n\nOrfelt created the portal; whether he opened it is guesswork.\n");
        WriteNote("Runeterra/NPCs/Vol.md", "Lord.");

        var context = TestNotes.Summarizer(Config(), _root).Prepare(new SessionDetails("Runeterra", Session, "Runeterra", "x"), current);

        Assert.Contains("THIS SESSION'S CURRENT NOTE", context.Options.Context);
        Assert.Contains("whether he opened it is guesswork", context.Options.Context);
        Assert.DoesNotContain("date: 2026-11-22", context.Options.Context);     // frontmatter left out
        Assert.Equal(1, context.Vault!.NoteCount);                                // only Vol
    }

    [Fact]
    public void FirstRun_DoesNotSendASameDayNote()
    {
        WriteNote("Runeterra/Sessions/2026-11-22 Runeterra.md", "## What Happened\n\nSecret earlier text.\n");

        var context = TestNotes.Summarizer(Config(), _root).Prepare(new SessionDetails("Runeterra", Session, "Runeterra", "x"));

        Assert.DoesNotContain("THIS SESSION'S CURRENT NOTE", context.Options.Context);
        Assert.DoesNotContain("Secret earlier text", context.Options.Context);
    }

    [Fact]
    public async Task Rerun_ReplacesTheNote_AndBacksItUp()
    {
        var config = Config();
        var current = WriteNote("Runeterra/Sessions/2026-11-22 Runeterra.md", "old note");
        var options = new SummarizationOptions("Runeterra", Session);
        var summary = new SummaryResult("## What Happened\n\nNew.\n", [], [], [], "");
        var backups = new ScribePaths(_root).EditBackups;

        var saved = await TestNotes.Store(config, _root).SaveAsync(
            new SessionDraft(summary, options, [], []), new SessionDetails("Runeterra", Session, "Runeterra", "x"), "a test", current, default);

        Assert.Equal(current, saved.NotePath);
        Assert.Contains("New.", File.ReadAllText(current));
        var backup = Directory.EnumerateFiles(backups, "*.md", SearchOption.AllDirectories).Single();
        Assert.Equal("old note", File.ReadAllText(backup));
        Assert.EndsWith(Path.Combine("Sessions", "2026-11-22 Runeterra.md"), backup);
        Assert.False(File.Exists(Path.Combine(Vault, "Runeterra", "Sessions", "2026-11-22 Runeterra (2).md")));
    }
}
