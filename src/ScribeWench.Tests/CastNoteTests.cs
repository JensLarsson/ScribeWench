using ScribeWench.Tests.Support;
using ScribeWench.Core.Models;

namespace ScribeWench.Tests;

public sealed class CastNoteTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "scribewench-tests", Guid.NewGuid().ToString("N"));

    public CastNoteTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private string CastPath => Path.Combine(_root, "Cast.md");

    private ObsidianConfig Config => new() { VaultPath = _root };

    private AppConfig App => new() { Obsidian = Config, Ai = { PromptFile = Path.Combine(_root, "home", "summary-prompt.md") } };

    private string Home => Path.Combine(_root, "home");

    [Fact]
    public void Read_NewTemplate_HasNothingToSend()
    {
        File.WriteAllText(CastPath, CastNote.Template);

        Assert.Null(CastNote.Read(CastPath));
        Assert.Null(CastNote.Read(Path.Combine(_root, "missing.md")));
    }

    [Fact]
    public void Read_DropsFrontmatterAndComments()
    {
        File.WriteAllText(CastPath, "---\ntags: [cast]\n---\n# Cast\n<!-- note to self -->\n- **Erik**: DM\n");

        Assert.Equal("# Cast\n\n- **Erik**: DM", CastNote.Read(CastPath));
    }

    [Fact]
    public void SplitUpdates_RemovesTheSection_AndIgnoresNone()
    {
        var reply = "## What Happened\nThings.\n\n## Loot & Mechanical Notes\n- A sword\n\n## Cast Updates\n- **Ralkern** also plays [[Lok'Kargul]].\n- **Linn** plays Book.\n";

        var (markdown, updates) = CastNote.SplitUpdates(reply);

        Assert.Equal("## What Happened\nThings.\n\n## Loot & Mechanical Notes\n- A sword\n", markdown);
        Assert.Equal(["**Ralkern** also plays [[Lok'Kargul]].", "**Linn** plays Book."], updates);

        Assert.Empty(CastNote.SplitUpdates("## Cliffhanger\nEnd.\n\n## Cast Updates\nNone\n").Updates);
        Assert.Empty(CastNote.SplitUpdates("## Cliffhanger\nEnd.\n\n## Cast Updates\n- None.\n").Updates);
        Assert.Equal("## Cliffhanger\nEnd.\n", CastNote.SplitUpdates("## Cliffhanger\nEnd.\n\n## Cast Updates\n- None\n").Markdown);
    }

    [Fact]
    public void SplitUpdates_WithoutTheSection_LeavesTheReplyAlone()
    {
        var reply = "## What Happened\nThings.\n";

        Assert.Equal((reply, 0), (CastNote.SplitUpdates(reply).Markdown, CastNote.SplitUpdates(reply).Updates.Count));
    }

    [Fact]
    public void Append_KeepsHandWrittenLines_AndAddsUnderOneHeading()
    {
        File.WriteAllText(CastPath, "# Cast\n\n- **Erik**: DM\n");

        CastNote.Append(CastPath, ["**Anna** plays Thorin."], new DateTime(2026, 11, 20));
        CastNote.Append(CastPath, ["**Anna** now also plays Elara."], new DateTime(2026, 11, 27));

        var lines = File.ReadAllLines(CastPath);
        Assert.Equal(
            ["# Cast", "", "- **Erik**: DM", "", "## From the sessions", "",
             "- 2026-11-20: **Anna** plays Thorin.", "- 2026-11-27: **Anna** now also plays Elara."],
            lines);
    }

    [Fact]
    public void Append_CreatesTheNote_WhenMissing()
    {
        CastNote.Append(CastPath, ["**Erik** runs the game."], new DateTime(2026, 11, 20));

        Assert.Contains("- 2026-11-20: **Erik** runs the game.", CastNote.Read(CastPath));
    }

    [Fact]
    public void ToWhisperHint_IsPlainNames_AndShort()
    {
        var cast = "# Cast\n\n- **Sly Red (Erik)**: DM\n- **Ralkern**: plays [[Gother]] and [[Lok'Kargul]]\n\n## Noted by ScribeWench\n\n- 2026-11-20: **Linn** plays [[Book|Book the Wizard]].";

        Assert.Equal(
            "Sly Red (Erik): DM. Ralkern: plays Gother and Lok'Kargul. Linn plays Book the Wizard.",
            CastNote.ToWhisperHint(cast));
        Assert.Null(CastNote.ToWhisperHint(null));
        Assert.True(CastNote.ToWhisperHint(string.Join("\n", Enumerable.Repeat("- **Somebody**: plays Someone", 100)))!.Length <= 501);
    }

    [Fact]
    public void Vault_SendsTheCast_AndKeepsItOutOfTheLinkList()
    {
        File.WriteAllText(CastPath, "# Cast\n\n- **Erik**: DM\n");
        File.WriteAllText(Path.Combine(_root, "Grak.md"), "");
        var vault = ObsidianVault.Open(Config);

        var notes = vault.ListNotes();
        var context = vault.BuildContext(notes, null);

        Assert.Equal(["Grak"], notes.Select(n => n.Name));
        Assert.Contains("- **Erik**: DM", context);
        Assert.Contains(CastNote.UpdatesHeading, context);
        Assert.True(context.IndexOf("CAST", StringComparison.Ordinal) < context.IndexOf("VAULT NOTES", StringComparison.Ordinal));
    }

    [Fact]
    public void EnsureGameFolder_CreatesTheCastNote_WithoutOverwriting()
    {
        TestNotes.Store(App, Home).PrepareGame("Orc");
        var path = Path.Combine(_root, "Orc", "Cast.md");
        Assert.Equal(CastNote.Template, File.ReadAllText(path));

        File.WriteAllText(path, "mine");
        TestNotes.Store(App, Home).PrepareGame("Orc");
        Assert.Equal("mine", File.ReadAllText(path));
    }

    [Fact]
    public async Task CastUpdates_AreWrittenToTheNote_AndTakenOutOfTheSummary()
    {
        var session = new SessionDetails("Orc", new DateTime(2026, 11, 20, 19, 0, 0), "Orc", "x");
        var summarizer = TestNotes.Summarizer(App, Home, "## What Happened\nThings.\n\n## Cast Updates\n- **Anna** plays Thorin.\n");

        var draft = await summarizer.WriteAsync([], summarizer.Prepare(session));
        await summarizer.SaveAsync(draft, session);

        Assert.DoesNotContain("Cast Updates", draft.Summary.Markdown);
        Assert.Contains("- 2026-11-20: **Anna** plays Thorin.", File.ReadAllText(Path.Combine(_root, "Orc", "Cast.md")));
    }
}
