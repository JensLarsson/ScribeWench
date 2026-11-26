using Microsoft.Extensions.Logging;
using ScribeWench.Core;
using ScribeWench.Core.Models;

namespace ScribeWench.AI.Summaries;

// The instructions a game's summaries are written by: its own file, edited from Discord, when it
// has a valid one, and otherwise the default file (ai.promptFile). A game's own file is kept
// outside the notes folder, which may be published.
public sealed class InstructionsStore(AiConfig ai, ScribePaths paths, TimeProvider time, ILogger<InstructionsStore> logger)
{
    public const string FileName = "instructions.md";

    // Path: the file the instructions came from.
    public sealed record Loaded(Instructions Instructions, string? Path, bool Custom);

    // The default file's full path, creating it with the built-in instructions if missing. Null when none is configured.
    public static string? EnsureFile(string configuredPath)
    {
        if (string.IsNullOrWhiteSpace(configuredPath)) return null;

        var path = PathHelper.Expand(configuredPath.Trim());
        if (!File.Exists(path))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, Instructions.Default.ToFile());
        }
        return path;
    }

    public string? EnsureDefaultFile() => EnsureFile(ai.PromptFile);

    // Whatever the default file lacks comes from the built-in instructions. An older file holding a
    // single prompt still works: it is taken as the session note rules.
    public Instructions LoadDefault()
    {
        var path = EnsureDefaultFile();
        if (path is null) return Instructions.Default;

        var text = File.ReadAllText(path);
        if (Instructions.StripComments(text).Length == 0)
        {
            logger.LogWarning("Prompt file {Path} has no instructions outside comments; using the built-in default", path);
            return Instructions.Default;
        }

        var instructions = Instructions.ParseLenient(text, Instructions.Default, out var problems);
        if (problems.Count > 0)
            logger.LogWarning("Prompt file {Path}: {Problems} Built-in defaults fill in what's missing.", path, string.Join(" ", problems));
        return instructions;
    }

    public Loaded Load(string? gameFolder)
    {
        // The game's own, when it has a file that can be used.
        if (!string.IsNullOrWhiteSpace(gameFolder) && File.Exists(PathFor(gameFolder)))
        {
            var path = PathFor(gameFolder);
            var parsed = Instructions.Parse(File.ReadAllText(path));
            if (parsed.Instructions is { } own)
            {
                // A file from before discussions had rules of their own takes those from the defaults.
                if (!parsed.Sections.ContainsKey(Instructions.DiscussionNoteSection))
                    own = own with { DiscussionNote = LoadDefault().DiscussionNote };
                logger.LogInformation("Using the game's instructions from {Path}", path);
                return new Loaded(own, path, Custom: true);
            }
            logger.LogWarning("Instructions file {Path} can't be used ({Problems}); using the defaults",
                path, string.Join(" ", parsed.Problems));
        }

        return new Loaded(LoadDefault(), EnsureDefaultFile(), Custom: false);
    }

    public string PathFor(string gameFolder) => Path.Combine(paths.GameSettings, gameFolder, FileName);

    // The game's file as it is on disk, or null when it uses the defaults.
    public string? ReadOwn(string gameFolder) =>
        File.Exists(PathFor(gameFolder)) ? File.ReadAllText(PathFor(gameFolder)) : null;

    // Returns the backup of the file it replaced, if there was one.
    public string? Save(string gameFolder, string text)
    {
        var path = PathFor(gameFolder);
        var backup = Backup(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return backup;
    }

    // Back to the defaults. Returns the backup of the file it removed, if there was one.
    public string? Reset(string gameFolder)
    {
        var path = PathFor(gameFolder);
        var backup = Backup(path);
        if (File.Exists(path)) File.Delete(path);
        return backup;
    }

    private string? Backup(string path)
    {
        if (!File.Exists(path)) return null;

        var folder = Path.Combine(Path.GetDirectoryName(path)!, "backups");
        Directory.CreateDirectory(folder);
        var now = time.GetLocalNow();
        var backup = Path.Combine(folder, $"instructions {now:yyyy-MM-dd HHmmss}.md");
        for (var i = 2; File.Exists(backup); i++)
            backup = Path.Combine(folder, $"instructions {now:yyyy-MM-dd HHmmss} ({i}).md");
        File.Copy(path, backup);
        return backup;
    }
}
