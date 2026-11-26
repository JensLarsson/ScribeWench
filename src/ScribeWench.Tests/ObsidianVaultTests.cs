using Microsoft.Extensions.DependencyInjection;
using ScribeWench.Tests.Support;
using ScribeWench.Core.Models;

namespace ScribeWench.Tests;

public sealed class ObsidianVaultTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "scribewench-tests", Guid.NewGuid().ToString("N"));

    public ObsidianVaultTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private void WriteNote(string relativePath, string content = "")
    {
        var path = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private ObsidianVault OpenVault() =>
        ObsidianVault.Open(new ObsidianConfig { VaultPath = _root, SessionsFolder = "Sessions" });

    [Fact]
    public void ListNotes_ReadsNamesFoldersAndAliases_SkipsDotFolders()
    {
        WriteNote("NPCs/Grak.md", "---\naliases: [Grak the Goblin, \"The Boss\"]\n---\nBody");
        WriteNote("Places/Domekjar.md", "---\naliases:\n  - Domek\n  - 'City of Stone'\ntags: [city]\n---\n");
        WriteNote("Lore.md", "No frontmatter");
        WriteNote(".obsidian/ignored.md");
        WriteNote(".trash/old.md");

        var notes = OpenVault().ListNotes();

        Assert.Equal(["Lore", "Grak", "Domekjar"], notes.Select(n => n.Name));
        Assert.Equal(["Grak the Goblin", "The Boss"], notes.Single(n => n.Name == "Grak").Aliases);
        Assert.Equal(["Domek", "City of Stone"], notes.Single(n => n.Name == "Domekjar").Aliases);
        Assert.Equal("Places", notes.Single(n => n.Name == "Domekjar").Folder);
    }

    [Fact]
    public void FindPreviousSessionNote_PicksNewestDatedBeforeTheSession()
    {
        WriteNote("Sessions/2026-11-01 Campaign.md");
        WriteNote("Sessions/2026-11-08 Campaign.md");
        WriteNote("Sessions/2026-11-15 Campaign.md");   // same day: a re-run of this session
        WriteNote("Sessions/Notes without date.md");

        var previous = OpenVault().FindPreviousSessionNote(new DateTime(2026, 11, 15, 19, 0, 0));

        Assert.Equal("2026-11-08 Campaign.md", Path.GetFileName(previous));
    }

    [Fact]
    public void NewSessionNotePath_NeverOverwritesAnExistingNote()
    {
        WriteNote("Sessions/2026-11-15 My Campaign.md", "edited by hand");
        var options = new SummarizationOptions("My Campaign", new DateTime(2026, 11, 15, 19, 0, 0));

        var path = OpenVault().NewSessionNotePath(options);

        Assert.Equal("2026-11-15 My Campaign (2).md", Path.GetFileName(path));
    }

    [Fact]
    public void MissingCustomFolder_Throws() =>
        Assert.Throws<DirectoryNotFoundException>(() =>
            ObsidianVault.Open(new ObsidianConfig { VaultPath = Path.Combine(_root, "missing") }));

    [Theory]
    [InlineData("", typeof(PlainNoteStore))]
    [InlineData("C:/vault", typeof(ObsidianNoteStore))]
    public void WithoutANotesFolder_SummariesGoToTheOutputFolder_AndNothingIsLinked(string vaultPath, Type store)
    {
        var services = new ServiceCollection().AddLogging()
            .AddSingleton(new ObsidianConfig { VaultPath = vaultPath })
            .AddSingleton(new OutputConfig())
            .AddSingleton(new ScribePaths(_root))
            .AddSingleton(TimeProvider.System)
            .AddNoteStore()
            .BuildServiceProvider();

        Assert.IsType(store, services.GetRequiredService<INoteStore>());
    }

    [Fact]
    public void NotesFolder_IsOnByDefault() =>
        Assert.Equal(ObsidianConfig.DefaultVaultPath, new ObsidianConfig().VaultPath);

    [Fact]
    public void ListNotes_SkipsTranscripts()
    {
        WriteNote("NPCs/Grak.md");
        WriteNote("Transcripts/Game_2026-11-15_19-00_transcript.md");

        Assert.Equal(["Grak"], OpenVault().ListNotes().Select(n => n.Name));
    }

    [Fact]
    public void StripWikiLinks_LeavesReadableText()
    {
        var text = "Met [[Grak]] and [[Domekjar|the city]] near [[Lore#Shapers War]].";

        Assert.Equal("Met Grak and the city near Lore.", SessionDocuments.StripWikiLinks(text));
    }

    [Fact]
    public void PromptFile_CreatedWithDefaults_CommentsNotSent()
    {
        var path = Path.Combine(_root, "prompt.md");

        var store = TestNotes.Instructions(new AppConfig { Ai = { PromptFile = path } }, _root);
        var instructions = store.LoadDefault();

        Assert.True(File.Exists(path));
        Assert.Contains("<!--", File.ReadAllText(path));
        Assert.DoesNotContain("<!--", instructions.SessionNote);
        Assert.StartsWith("You are the chronicler", instructions.SessionNote);

        // An older file holding a single prompt is the session note rules; the rest are the defaults.
        File.WriteAllText(path, "<!-- note to self -->\nWrite a limerick.");
        var legacy = store.LoadDefault();
        Assert.Equal("Write a limerick.", legacy.SessionNote);
        Assert.Equal(Instructions.Default.VaultUpdates, legacy.VaultUpdates);
    }
}
