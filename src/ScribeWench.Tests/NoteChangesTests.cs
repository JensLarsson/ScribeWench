using ScribeWench.Core.Models;
using ScribeWench.Tests.Support;

namespace ScribeWench.Tests;

// A session changing what a note says, and the list of those changes at the end of its summary.
public sealed class NoteChangesTests : IDisposable
{
    private const string Label = "[[2027-06-12 Runeterra]]";
    private static readonly DateTime Session = new(2027, 6, 12, 19, 0, 0);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "scribewench-tests", Guid.NewGuid().ToString("N"));
    private string Vault => Path.Combine(_root, "vault");
    private string Backups => Path.Combine(_root, "backups");

    public NoteChangesTests() => Directory.CreateDirectory(Vault);

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

    private ObsidianVault Game() => ObsidianVault.Open(Config().Obsidian, "Runeterra");

    private static string Read(string path) => File.ReadAllText(path).ReplaceLineEndings("\n");

    private static VaultUpdates.Update Changing(string name, string summary, string find, string replace) =>
        new(name, false, null, null, []) { Changes = [new VaultUpdates.Change(summary, find, replace)] };

    private const string Reply = """
        ## What Happened

        Marta died at the docks.

        ## Cast Updates
        - None

        ## Vault Updates
        ### [[Marta]]
        - Killed at the docks by the cultists.
        Whereabouts: Buried in [[Bilgewater]].
        Changed: [[Marta]] is now dead
        <<<<<<< FIND
        Marta runs the Salty Gull.
        =======
        Marta ran the Salty Gull until she was killed at the docks ({session}).
        >>>>>>> END
        ### [[Salty Gull]]
        Changed: The [[Salty Gull]] now belongs to Marta's brother
        <<<<<<< FIND
        ## Owner
        - Marta
        =======
        ## Owner
        - Marta's brother
        >>>>>>> END

        ## Afterword
        Kept.
        """;

    // ── reading the reply ────────────────────────────────────────────────────

    [Fact]
    public void Split_ReadsChanges_WithTheirSentence_AndTextKeptAsWritten()
    {
        var (markdown, updates) = VaultUpdates.Split(Reply);

        Assert.Equal(["Marta", "Salty Gull"], updates.Select(u => u.Name));
        Assert.Equal(["Killed at the docks by the cultists."], updates[0].Facts);
        Assert.Equal(["Buried in [[Bilgewater]]."], updates[0].Whereabouts);
        var change = Assert.Single(updates[0].Changes);
        Assert.Equal("[[Marta]] is now dead", change.Summary);
        Assert.Equal("Marta runs the Salty Gull.", change.Find);
        Assert.Equal("Marta ran the Salty Gull until she was killed at the docks ({session}).", change.Replace);

        // A heading inside a block is the note's text, not the end of the section or a fact.
        var owner = Assert.Single(updates[1].Changes);
        Assert.Equal("## Owner\n- Marta", owner.Find.ReplaceLineEndings("\n"));
        Assert.Equal("## Owner\n- Marta's brother", owner.Replace.ReplaceLineEndings("\n"));
        Assert.Empty(updates[1].Facts);

        // What comes after the section stays in the note.
        Assert.Contains("## Afterword\nKept.", markdown.ReplaceLineEndings("\n"));
        Assert.DoesNotContain("FIND", markdown);
    }

    [Fact]
    public void Instructions_ShowTheChangeLayout()
    {
        var text = VaultUpdates.BuildInstructions(["NPCs"]);

        Assert.Contains("Changed: [the note's name and what is true of it now", text);
        Assert.Contains("<<<<<<< FIND", text);
        Assert.Contains(">>>>>>> END", text);
        Assert.Contains("{session}", text);
        Assert.Contains("\"## What Changed\"", text);
        Assert.Contains("Keep what each note says current.", text);       // the default rule that asks for changes
    }

    // ── changing the notes ───────────────────────────────────────────────────

    [Fact]
    public void Apply_ReplacesTheText_KeepsTheRest_AndBacksTheNoteUp()
    {
        var marta = WriteNote("Runeterra/NPCs/Marta.md", "---\naliases: [The innkeeper]\n---\nMarta runs the Salty Gull. She waters the ale.\n");
        var (_, updates) = VaultUpdates.Split(Reply);

        var result = VaultUpdates.Apply(Game(), [updates[0]], Label, Backups);

        Assert.Equal(["[[Marta]] is now dead"], result.Changes);
        Assert.Empty(result.NotChanged);
        Assert.Equal(["Marta"], result.Updated);
        Assert.Equal(
            "---\naliases: [The innkeeper]\n---\n" +
            "Marta ran the Salty Gull until she was killed at the docks ([[2027-06-12 Runeterra]]). She waters the ale.\n\n" +
            "## From the sessions\n\n- [[2027-06-12 Runeterra]]: Killed at the docks by the cultists.\n\n" +
            "## Last whereabouts\n\n- [[2027-06-12 Runeterra]]: Buried in [[Bilgewater]].\n",
            Read(marta));
        Assert.Equal(
            "---\naliases: [The innkeeper]\n---\nMarta runs the Salty Gull. She waters the ale.\n",
            Read(Path.Combine(Backups, "NPCs", "Marta.md")));
    }

    [Fact]
    public void Apply_KeepsWindowsLineEndings()
    {
        var gull = WriteNote("Runeterra/Places/Salty Gull.md", "An inn.\r\n\r\n## Owner\r\n- Marta\r\n");
        var (_, updates) = VaultUpdates.Split(Reply);

        var result = VaultUpdates.Apply(Game(), [updates[1]], Label, Backups);

        Assert.Single(result.Changes);
        Assert.Equal("An inn.\r\n\r\n## Owner\r\n- Marta's brother\r\n", File.ReadAllText(gull));
    }

    [Fact]
    public void Apply_LeavesTheNoteAlone_WhenTheTextIsMissing_Repeated_OrAllOfIt()
    {
        var marta = WriteNote("Runeterra/NPCs/Marta.md", "Marta is here. Marta is here.\n");

        var result = VaultUpdates.Apply(Game(),
        [
            Changing("Marta", "[[Marta]] left", "Marta has gone.", "Marta left."),
            Changing("Marta", "[[Marta]] moved", "Marta is here.", "Marta moved."),
            Changing("Marta", "[[Marta]] is forgotten", "Marta is here. Marta is here.\n", ""),
            Changing("Marta", "nothing", "  ", "x"),
            Changing("Nobody", "[[Nobody]] changed", "x", "y"),
        ], Label, Backups);

        Assert.Empty(result.Changes);
        Assert.Empty(result.Updated);
        Assert.Empty(result.Created);                                       // no note is made for a change alone
        Assert.Equal(
        [
            "Marta: couldn't find \"Marta has gone.\"",
            "Marta: \"Marta is here.\" appears more than once",
            "Marta: the change would leave the note empty",
            "Marta: no text to replace was given",
            "Nobody: no such note",
        ], result.NotChanged);
        Assert.Equal("Marta is here. Marta is here.\n", Read(marta));
        Assert.False(Directory.Exists(Backups));
    }

    [Fact]
    public void Apply_NeverChangesSessionNotes_DiscussionNotes_OrTheCast()
    {
        var session = WriteNote("Runeterra/Sessions/2027-06-05 Runeterra.md", "Marta poured ale.\n");
        var discussion = WriteNote("Runeterra/Sessions/2027-06-08 Runeterra discussion.md", "Marta poured ale.\n");
        var cast = WriteNote("Runeterra/Cast.md", "# Cast\n- **Ralkern**: DM\n");

        var result = VaultUpdates.Apply(Game(),
        [
            Changing("2027-06-05 Runeterra", "rewritten", "Marta poured ale.", "Nothing happened."),
            Changing("2027-06-08 Runeterra discussion", "rewritten", "Marta poured ale.", "Nothing happened."),
            Changing("Cast", "rewritten", "DM", "player"),
        ], Label, Backups);

        Assert.Empty(result.Changes);
        Assert.Equal("Marta poured ale.\n", Read(session));
        Assert.Equal("Marta poured ale.\n", Read(discussion));
        Assert.Equal("# Cast\n- **Ralkern**: DM\n", Read(cast));
    }

    [Fact]
    public void Apply_BacksUpTheNoteAsItWas_EvenWhenItIsChangedTwice()
    {
        WriteNote("Runeterra/NPCs/Marta.md", "Alive. Rich.\n");

        var result = VaultUpdates.Apply(Game(),
        [
            Changing("Marta", "[[Marta]] is now dead", "Alive.", "Dead."),
            Changing("Marta", "[[Marta]] is now poor", "Rich.", "Poor."),
        ], Label, Backups);

        Assert.Equal(["[[Marta]] is now dead", "[[Marta]] is now poor"], result.Changes);
        Assert.Equal("Alive. Rich.\n", Read(Path.Combine(Backups, "NPCs", "Marta.md")));
    }

    // ── the list at the end of the summary ───────────────────────────────────

    [Fact]
    public void TheList_GivesTheChanges_ThenTheNotesMadeAndAddedTo()
    {
        var result = new VaultUpdates.Result(["Brom"], ["Marta", "Bilgewater"]) { Changes = ["[[Marta]] is now dead"] };

        Assert.Equal(
            ["[[Marta]] is now dead", "New notes: [[Brom]]", "Updated notes: [[Marta]], [[Bilgewater]]"],
            SessionDocuments.ChangeLines(result));
        Assert.Empty(SessionDocuments.ChangeLines(VaultUpdates.Result.Empty));
    }

    [Fact]
    public async Task ASession_ListsWhatChanged_LastInItsNote_AndInDiscord()
    {
        WriteNote("Runeterra/NPCs/Marta.md", "Marta runs the Salty Gull.\n");
        WriteNote("Runeterra/Places/Salty Gull.md", "An inn.\n\n## Owner\n- Marta\n");
        var summarizer = TestNotes.Summarizer(Config(), _root, Reply);
        var details = new SessionDetails("Runeterra", Session, "Runeterra", "x");

        var draft = await summarizer.WriteAsync([], summarizer.Prepare(details));
        var saved = await summarizer.SaveAsync(draft, details);

        const string list =
            "## What Changed\n" +
            "- [[Marta]] is now dead\n" +
            "- The [[Salty Gull]] now belongs to Marta's brother\n" +
            "- Updated notes: [[Marta]]\n";
        var note = Read(saved.NotePath!);
        Assert.EndsWith("## Afterword\nKept.\n\n" + list, note);
        Assert.Contains("tags:\n  - session", note);

        var post = summarizer.ForDiscord(draft, saved);
        Assert.EndsWith(
            "## What Changed\n- Marta is now dead\n- The Salty Gull now belongs to Marta's brother\n- Updated notes: Marta\n", post);

        // The notes as they were, in a folder of this save's own.
        var backup = Directory.EnumerateFiles(new ScribePaths(_root).EditBackups, "*.md", SearchOption.AllDirectories).ToList();
        Assert.Equal(2, backup.Count);
        Assert.All(backup, path => Assert.Contains(" session", path));
    }

    [Fact]
    public async Task ASession_ThatChangesNothing_GetsNoList()
    {
        var summarizer = TestNotes.Summarizer(Config(), _root, "## What Happened\n\nThey rested.\n\n## Vault Updates\nNone\n");
        var details = new SessionDetails("Runeterra", Session, "Runeterra", "x");

        var draft = await summarizer.WriteAsync([], summarizer.Prepare(details));
        var saved = await summarizer.SaveAsync(draft, details);

        Assert.DoesNotContain("What Changed", Read(saved.NotePath!));
        Assert.DoesNotContain("What Changed", summarizer.ForDiscord(draft, saved));
    }

    [Fact]
    public async Task AListTheModelWroteItself_IsReplacedByTheRealOne()
    {
        WriteNote("Runeterra/NPCs/Marta.md", "Marta runs the Salty Gull.\n");
        var reply = "## What Happened\n\nMarta died.\n\n## What Changed\n- Everything, supposedly\n\n" +
                    "## Vault Updates\n### [[Marta]]\nChanged: [[Marta]] is now dead\n<<<<<<< FIND\nruns\n=======\nran\n>>>>>>> END\n";
        var summarizer = TestNotes.Summarizer(Config(), _root, reply);
        var details = new SessionDetails("Runeterra", Session, "Runeterra", "x");

        var draft = await summarizer.WriteAsync([], summarizer.Prepare(details));
        var saved = await summarizer.SaveAsync(draft, details);

        var note = Read(saved.NotePath!);
        Assert.DoesNotContain("supposedly", note);
        Assert.EndsWith("Marta died.\n\n## What Changed\n- [[Marta]] is now dead\n", note);
    }

    [Fact]
    public async Task ARerun_KeepsTheChangesTheNoteAlreadyLists_AndDoesNotSendThemBack()
    {
        var config = Config();
        var current = WriteNote("Runeterra/Sessions/2027-06-12 Runeterra.md",
            "## What Happened\n\nOld.\n\n## What Changed\n- [[Vol]] is now dead\n");
        WriteNote("Runeterra/NPCs/Brom.md", "Brom is a sailor.\n");
        var reply = "## What Happened\n\nNew.\n\n## Vault Updates\n### [[Brom]]\nChanged: [[Brom]] is now an innkeeper\n<<<<<<< FIND\nsailor\n=======\ninnkeeper\n>>>>>>> END\n";
        var summarizer = TestNotes.Summarizer(config, _root, reply);
        var details = new SessionDetails("Runeterra", Session, "Runeterra", "x");

        var context = summarizer.Prepare(details, current);
        var draft = await summarizer.WriteAsync([], context);
        var saved = await summarizer.SaveAsync(draft, details, current);

        Assert.Contains("THIS SESSION'S CURRENT NOTE", context.Options.Context);
        Assert.DoesNotContain("[[Vol]] is now dead", context.Options.Context);
        Assert.EndsWith("New.\n\n## What Changed\n- [[Vol]] is now dead\n- [[Brom]] is now an innkeeper\n", Read(current));

        // One folder holds both the old note and the changed one.
        var folders = Directory.GetDirectories(new ScribePaths(_root).EditBackups);
        Assert.EndsWith(" re-run", Assert.Single(folders));
        Assert.Equal(2, Directory.EnumerateFiles(folders[0], "*.md", SearchOption.AllDirectories).Count());
    }
}
