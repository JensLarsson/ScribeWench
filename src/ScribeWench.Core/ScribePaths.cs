namespace ScribeWench.Core;

// Where ScribeWench keeps its own files. The notes themselves go where setup says.
public sealed record ScribePaths(string Home)
{
    public static ScribePaths Default { get; } = new(PathHelper.Expand("~/.scribewench"));

    public string ConfigFile => Path.Combine(Home, "config.json");
    public string GamesFile => Path.Combine(Home, "games.json");
    // One folder per game, for what must stay out of the notes folder, which may be published.
    public string GameSettings => Path.Combine(Home, "games");
    public string EditBackups => Path.Combine(Home, "edit-backups");
    public string LlmModels => Path.Combine(Home, "models", "llm");
}
