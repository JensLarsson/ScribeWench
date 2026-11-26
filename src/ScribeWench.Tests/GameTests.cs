using ScribeWench.Bot.Commands;
using ScribeWench.Tests.Support;
using ScribeWench.Core;
using ScribeWench.Core.Models;

namespace ScribeWench.Tests;

public sealed class GameTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "scribewench-tests", Guid.NewGuid().ToString("N"));
    private readonly GameStore _store;

    public GameTests()
    {
        Directory.CreateDirectory(_root);
        _store = new GameStore(new ScribePaths(_root), TimeProvider.System);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public async Task Create_ThenFind_IgnoresCaseAndExtraSpaces()
    {
        var (game, error) = await _store.CreateAsync(1, "  Curse   of Strahd ", 42);

        Assert.Null(error);
        Assert.Equal("Curse of Strahd", game!.Name);
        Assert.Equal("Curse of Strahd", game.Folder);
        Assert.NotNull(await _store.FindAsync(1, "curse of strahd"));
    }

    [Fact]
    public async Task Games_ArePerDiscordServer()
    {
        await _store.CreateAsync(1, "Main Campaign", 42);

        Assert.Null(await _store.FindAsync(2, "Main Campaign"));
        Assert.Empty(await _store.ListAsync(2));
        Assert.Null((await _store.CreateAsync(2, "Main Campaign", 7)).Error);   // same name elsewhere is fine
    }

    [Fact]
    public async Task Create_Duplicate_IsRefused()
    {
        await _store.CreateAsync(1, "Goblin Heist", 42);

        var (game, error) = await _store.CreateAsync(1, "GOBLIN HEIST", 42);

        Assert.Null(game);
        Assert.Contains("already", error);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Strahd/Barovia")]
    [InlineData("[[Link]]")]
    [InlineData("What?")]
    [InlineData(".hidden")]
    [InlineData("CON")]
    [InlineData("---")]
    [InlineData("One-shot: Goblins")]   // ':' isn't allowed in Windows folder names
    public void ValidateName_RejectsNamesThatCantBeFolders(string name) =>
        Assert.NotNull(GameStore.ValidateName(name));

    [Fact]
    public void ValidateName_NamesOnlyTheOffendingCharacters() =>
        Assert.Equal("Can't contain : ?", GameStore.ValidateName("Who: me? Who: you?"));

    [Theory]
    [InlineData("Curse of Strahd")]
    [InlineData("One-shot - Goblin Heist")]
    [InlineData("Dragon's Rest (2026)")]
    public void ValidateName_AcceptsOrdinaryNames(string name) =>
        Assert.Null(GameStore.ValidateName(name));

    [Fact]
    public async Task Remove_And_RecordSession_PersistAcrossInstances()
    {
        await _store.CreateAsync(1, "Keep", 42);
        await _store.CreateAsync(1, "Typo Gmae", 42);
        var started = new DateTimeOffset(2026, 11, 15, 19, 0, 0, TimeSpan.Zero);
        await _store.RecordSessionAsync(1, "keep", started);

        Assert.NotNull(await _store.RemoveAsync(1, "typo gmae"));

        var reopened = new GameStore(new ScribePaths(_root), TimeProvider.System);
        var games = await reopened.ListAsync(1);
        var keep = Assert.Single(games);
        Assert.Equal(1, keep.SessionCount);
        Assert.Equal(started, keep.LastSessionAt);
    }

    [Theory]
    [InlineData("!scribe join Curse of Strahd", "join", "Curse of Strahd")]
    [InlineData("!SCRIBE JOIN \"Curse of Strahd\"", "join", "Curse of Strahd")]
    [InlineData("!scribe create-game   Goblin Heist  ", "create-game", "Goblin Heist")]
    [InlineData("!scribe list-games", "list-games", "")]
    [InlineData("!scribe", "", "")]
    public void TryParse_SplitsCommandAndArgument(string message, string command, string argument)
    {
        Assert.True(CommandRouter.TryParse(message, "!scribe", out var c, out var a));
        Assert.Equal(command, c);
        Assert.Equal(argument, a);
    }

    [Theory]
    [InlineData("!scribes join x")]
    [InlineData("!scribejoin")]
    [InlineData("hello !scribe join")]
    public void TryParse_IgnoresOtherMessages(string message) =>
        Assert.False(CommandRouter.TryParse(message, "!scribe", out _, out _));

    [Fact]
    public void Vault_IsNarrowedToTheGameFolder()
    {
        var vaultRoot = Path.Combine(_root, "vault");
        Directory.CreateDirectory(Path.Combine(vaultRoot, "Curse of Strahd", "NPCs"));
        Directory.CreateDirectory(Path.Combine(vaultRoot, "Other Game"));
        File.WriteAllText(Path.Combine(vaultRoot, "Curse of Strahd", "NPCs", "Strahd.md"), "");
        File.WriteAllText(Path.Combine(vaultRoot, "Other Game", "Unrelated NPC.md"), "");
        var config = new ObsidianConfig { VaultPath = vaultRoot, SessionsFolder = "Sessions" };
        var store = TestNotes.Store(new AppConfig { Obsidian = config }, _root);

        var setup = store.PrepareGame("Curse of Strahd")!;
        var notes = ObsidianVault.Open(config, "Curse of Strahd").ListNotes();

        Assert.True(setup.Existed);
        Assert.Equal(1, setup.NoteCount);
        Assert.Equal(["Strahd"], notes.Select(n => n.Name));
        Assert.True(Directory.Exists(Path.Combine(vaultRoot, "Curse of Strahd", "Sessions")));

        var fresh = store.PrepareGame("Goblin Heist")!;
        Assert.False(fresh.Existed);
        Assert.True(Directory.Exists(Path.Combine(vaultRoot, "Goblin Heist", "Sessions")));
    }
}
