using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ScribeWench.AI.Summaries;
using ScribeWench.Core;
using ScribeWench.Core.Models;

namespace ScribeWench.AI.Notes;

// One game's folder in the notes folder (or the whole folder, without a game): its notes as link
// targets and as text for the model, and where its session notes, discussion notes, transcripts and
// images go.
public sealed class ObsidianVault
{
    private static readonly Regex DatePrefixRegex = new(@"^(\d{4}-\d{2}-\d{2})", RegexOptions.Compiled);

    private readonly ObsidianConfig _config;

    public string RootPath { get; }
    public string SessionsPath { get; }
    public string TranscriptsPath { get; }
    // May not exist yet.
    public string AttachmentsPath { get; }
    // May not exist yet.
    public string CastPath { get; }

    private ObsidianVault(ObsidianConfig config, string rootPath)
    {
        _config = config;
        RootPath = rootPath;
        SessionsPath = Path.GetFullPath(Path.Combine(rootPath, config.SessionsFolder.Trim()));
        TranscriptsPath = Path.GetFullPath(Path.Combine(rootPath, config.TranscriptsFolder.Trim()));
        AttachmentsPath = Path.GetFullPath(Path.Combine(rootPath,
            string.IsNullOrWhiteSpace(config.AttachmentsFolder) ? "Attachments" : config.AttachmentsFolder.Trim()));
        var castName = string.IsNullOrWhiteSpace(config.CastNote) ? "Cast" : config.CastNote.Trim();
        CastPath = Path.GetFullPath(Path.Combine(rootPath, castName + ".md"));
    }

    // With a game, narrowed to that game's folder, which is created if missing. Throws when the notes
    // folder itself is missing.
    public static ObsidianVault Open(ObsidianConfig config, string? gameFolder = null)
    {
        var root = ResolveRoot(config);
        if (!string.IsNullOrWhiteSpace(gameFolder))
        {
            root = Path.GetFullPath(Path.Combine(root, gameFolder));
            Directory.CreateDirectory(root);
        }
        return new ObsidianVault(config, root);
    }

    // The notes folder, checked to be there.
    public static string ResolveRoot(ObsidianConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.VaultPath))
            throw new InvalidOperationException("No notes folder is set up.");

        var root = PathHelper.Expand(config.VaultPath.Trim().Trim('"'));
        if (Directory.Exists(root)) return root;

        // The default folder is ours to create. A custom path that's missing is more likely a vault
        // on an unplugged drive or a typo, and silently creating it would split the notes in two.
        if (root.Equals(PathHelper.Expand(ObsidianConfig.DefaultVaultPath), StringComparison.OrdinalIgnoreCase))
        {
            Directory.CreateDirectory(root);
            return root;
        }

        throw new DirectoryNotFoundException(
            $"Notes folder not found: {root}. Fix it with `scribewench setup`.");
    }

    public sealed record Note(string Name, string Folder, IReadOnlyList<string> Aliases, string FullPath);

    // Every Markdown note, skipping dot-folders such as .obsidian and .trash.
    public IReadOnlyList<Note> ListNotes(IEnumerable<string>? excludePaths = null)
    {
        var excluded = new HashSet<string>(
            (excludePaths ?? []).Select(Path.GetFullPath), StringComparer.OrdinalIgnoreCase);

        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
        var notes = new List<Note>();
        var transcriptsPrefix = TranscriptsPath.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

        foreach (var file in Directory.EnumerateFiles(RootPath, "*.md", options))
        {
            var relative = Path.GetRelativePath(RootPath, file);
            var parts = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (parts.Any(p => p.StartsWith('.'))) continue;
            if (excluded.Contains(Path.GetFullPath(file))) continue;
            // The cast note is sent in full instead; it isn't something to link to.
            if (Path.GetFullPath(file).Equals(CastPath, StringComparison.OrdinalIgnoreCase)) continue;
            // Transcripts aren't things to link to, and would flood the list.
            if (Path.GetFullPath(file).StartsWith(transcriptsPrefix, StringComparison.OrdinalIgnoreCase)) continue;

            var folder = string.Join('/', parts[..^1]);
            notes.Add(new Note(Path.GetFileNameWithoutExtension(file), folder, ReadAliases(file), file));
        }

        return notes.OrderBy(n => n.Folder, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(n => n.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();
    }

    // The notes ScribeWench writes about recordings, sessions and discussions alike. They are never
    // kept up to date like the others.
    public bool InSessions(string path) =>
        Path.GetFullPath(path).StartsWith(SessionsPath.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    // A note on a campaign discussion, going by the tag ScribeWench gives it.
    public static bool IsDiscussionNote(string path) =>
        ReadList(path, "tags:", "tag:").Contains(SessionDocuments.DiscussionTag, StringComparer.OrdinalIgnoreCase);

    // The newest session note dated before the session, going by the yyyy-MM-dd its file name starts
    // with. A discussion in between is passed over: the story continues from the last time it was played.
    public string? FindPreviousSessionNote(DateTime sessionDate)
    {
        if (!Directory.Exists(SessionsPath)) return null;

        return Directory.EnumerateFiles(SessionsPath, "*.md")
            .Select(path => (path, match: DatePrefixRegex.Match(Path.GetFileName(path))))
            .Where(x => x.match.Success
                && DateTime.TryParseExact(x.match.Value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
                && d.Date < sessionDate.Date)
            .OrderByDescending(x => Path.GetFileName(x.path), StringComparer.Ordinal)
            .Select(x => x.path)
            .FirstOrDefault(path => !IsDiscussionNote(path));
    }

    // How much of the vault went into the context.
    public sealed record ContextStats(int NotesInFull, int NotesByNameOnly, int Characters);

    // The CAST and VAULT NOTES blocks that go before the transcript: every note with its text, earlier
    // session notes oldest first. Notes that don't fit in the limit are sent by name only;
    // maxNoteCharacters lowers the limit, for a model with a small context window.
    public string BuildContext(IReadOnlyList<Note> notes, string? previousSessionPath, DateTime sessionDate, out ContextStats stats,
        Instructions? instructions = null, int? maxNoteCharacters = null)
    {
        instructions ??= Instructions.Default;
        var limit = Math.Max(0, Math.Min(_config.MaxNoteCharacters, maxNoteCharacters ?? int.MaxValue));
        if (!_config.SendFullNotes)
        {
            stats = new ContextStats(previousSessionPath is null ? 0 : 1, notes.Count, 0);
            return BuildContext(notes, previousSessionPath, instructions);
        }

        var sessionsPrefix = SessionsPath.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var entries = notes.Select(note =>
        {
            var match = DatePrefixRegex.Match(note.Name);
            DateTime? date = note.FullPath.StartsWith(sessionsPrefix, StringComparison.OrdinalIgnoreCase)
                && match.Success
                && DateTime.TryParseExact(match.Value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
                    ? d : null;
            // A session note for this same day is an earlier run of this summary; its text would only be echoed back.
            var body = date is { } noteDate && noteDate.Date >= sessionDate.Date ? "" : ReadBody(note.FullPath);
            return (Note: note, SessionDate: date, Body: body);
        }).ToList();

        // Over budget: drop text from the oldest sessions first, then from the other notes, largest first.
        var dropOrder = entries.Where(e => e.SessionDate is not null).OrderBy(e => e.SessionDate)
            .Concat(entries.Where(e => e.SessionDate is null).OrderByDescending(e => e.Body.Length))
            .ToList();
        var dropped = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var total = 0;

        void DropUntilWithinLimit(bool sparePreviousSession)
        {
            dropped.Clear();
            total = entries.Sum(e => e.Body.Length);
            foreach (var e in dropOrder)
            {
                if (total <= limit) break;
                if (sparePreviousSession && e.Note.FullPath.Equals(previousSessionPath, StringComparison.OrdinalIgnoreCase)) continue;
                total -= e.Body.Length;
                dropped.Add(e.Note.FullPath);
            }
        }

        // Keep the previous session for as long as anything else can go instead. When it doesn't fit
        // even alone, it goes in its turn like the other sessions, and smaller notes stay.
        DropUntilWithinLimit(sparePreviousSession: true);
        if (total > limit) DropUntilWithinLimit(sparePreviousSession: false);

        var sb = new StringBuilder();
        sb.AppendLine(CastNote.BuildContext(CastNote.Read(CastPath), instructions.CastUpdates));

        string BodyOf((Note Note, DateTime? SessionDate, string Body) e) => dropped.Contains(e.Note.FullPath) ? "" : e.Body;
        var inFull = 0;

        void AppendNote((Note Note, DateTime? SessionDate, string Body) e, string? extra = null)
        {
            sb.Append("<note name=\"").Append(e.Note.Name).Append('"');
            if (e.Note.Folder.Length > 0) sb.Append(" folder=\"").Append(e.Note.Folder).Append('"');
            if (e.Note.Aliases.Count > 0) sb.Append(" aliases=\"").Append(string.Join(", ", e.Note.Aliases)).Append('"');
            if (extra is not null) sb.Append(' ').Append(extra);
            var body = BodyOf(e);
            if (body.Length == 0) { sb.AppendLine(" />"); return; }
            inFull++;
            sb.AppendLine(">").AppendLine(body).AppendLine("</note>");
        }

        var others = entries.Where(e => e.SessionDate is null).ToList();
        var sessions = entries.Where(e => e.SessionDate is not null).OrderBy(e => e.SessionDate).ThenBy(e => e.Note.Name, StringComparer.Ordinal).ToList();

        if (others.Count > 0)
        {
            sb.AppendLine("VAULT NOTES (the group's own notes on this campaign. Use them to know who and what things are, to spell names, and as link targets: [[Note name]]. Do not retell them; the session note you write covers only what the transcript says):");
            foreach (var e in others) AppendNote(e);
            sb.AppendLine();
        }

        if (sessions.Count > 0)
        {
            sb.AppendLine("EARLIER SESSION NOTES (oldest first, also link targets. Use them for continuity: who is who, what was left open. Do not retell them):");
            foreach (var e in sessions)
                AppendNote(e,
                    e.Note.FullPath.Equals(previousSessionPath, StringComparison.OrdinalIgnoreCase) ? "previous-session=\"true\""
                    : IsDiscussionNote(e.Note.FullPath) ? "discussion=\"true\""
                    : null);
            sb.AppendLine();
        }

        if (_config.UpdateNotes)
            sb.AppendLine(VaultUpdates.BuildInstructions(VaultUpdates.Folders(this), instructions.VaultUpdates));

        stats = new ContextStats(inFull, notes.Count - inFull, total);
        return sb.ToString();
    }

    // The short version, for reading a transcript one part at a time: the cast, and the names and
    // aliases of the notes so they are spelled the same way. Session notes are left out; their names are dates.
    public string BuildPartContext(IReadOnlyList<Note> notes, int maxCharacters = 6000)
    {
        var sb = new StringBuilder();
        sb.AppendLine("CAST (who plays whom, kept by the group; the names in bold are the speaker names in the transcript):");
        var cast = CastNote.Read(CastPath);
        sb.AppendLine(string.IsNullOrWhiteSpace(cast) ? "(nothing written yet)" : cast.Trim());

        var names = notes
            .Where(n => !InSessions(n.FullPath))
            .Select(n => n.Aliases.Count > 0 ? $"{n.Name} (also: {string.Join(", ", n.Aliases)})" : n.Name)
            .ToList();
        if (names.Count == 0) return sb.ToString();

        sb.AppendLine();
        sb.AppendLine("KNOWN NAMES (people, places, factions and items the group has notes on; when the transcript clearly means one of them, spell it like this):");
        var first = true;
        foreach (var name in names)
        {
            if (sb.Length + name.Length + 2 > maxCharacters) break;
            sb.Append(first ? "" : "; ").Append(name);
            first = false;
        }
        sb.AppendLine();
        return sb.ToString();
    }

    // A note's text without its frontmatter.
    public static string ReadBody(string path)
    {
        try
        {
            var text = File.ReadAllText(path);
            if (text.StartsWith("---"))
            {
                var end = text.IndexOf("\n---", 3, StringComparison.Ordinal);
                var lineEnd = end < 0 ? -1 : text.IndexOf('\n', end + 4);
                if (end >= 0) text = lineEnd < 0 ? "" : text[(lineEnd + 1)..];
            }
            return text.Trim();
        }
        catch (IOException)
        {
            return "";
        }
    }

    // With SendFullNotes off: the cast, the note names and aliases, and the previous session.
    public string BuildContext(IReadOnlyList<Note> notes, string? previousSessionPath, Instructions? instructions = null)
    {
        var sb = new StringBuilder();

        sb.AppendLine(CastNote.BuildContext(CastNote.Read(CastPath), (instructions ?? Instructions.Default).CastUpdates));

        if (notes.Count > 0)
        {
            sb.AppendLine("VAULT NOTES (existing Obsidian notes; link them with [[Note name]], folder shown in parentheses for context):");
            foreach (var note in notes)
            {
                sb.Append("- ").Append(note.Name);
                if (note.Folder.Length > 0) sb.Append(" (").Append(note.Folder).Append(')');
                if (note.Aliases.Count > 0) sb.Append(" — aliases: ").Append(string.Join(", ", note.Aliases));
                sb.AppendLine();
            }
            sb.AppendLine();
        }

        if (previousSessionPath is not null)
        {
            sb.AppendLine($"PREVIOUS SESSION NOTE ([[{Path.GetFileNameWithoutExtension(previousSessionPath)}]]):");
            sb.AppendLine(File.ReadAllText(previousSessionPath).Trim());
            sb.AppendLine();
        }

        return sb.ToString();
    }

    // Never an existing note, which may have been edited by hand: that gets a " (2)" sibling instead.
    public string NewSessionNotePath(SummarizationOptions options)
    {
        var path = SessionNotePath(options);
        var baseName = Path.GetFileNameWithoutExtension(path);
        for (var i = 2; File.Exists(path); i++)
            path = Path.Combine(SessionsPath, $"{baseName} ({i}).md");
        return path;
    }

    // "Sessions/2026-11-15 My Campaign discussion.md": beside the session notes, in date order with
    // them, and told apart by its last word. A " (2)" sibling when the day has one already.
    public string NewDiscussionNotePath(SummarizationOptions options)
    {
        var baseName = SanitizeNoteName($"{options.SessionDateTime:yyyy-MM-dd} {options.CampaignName} discussion");
        var path = Path.Combine(SessionsPath, baseName + ".md");
        for (var i = 2; File.Exists(path); i++)
            path = Path.Combine(SessionsPath, $"{baseName} ({i}).md");
        return path;
    }

    // "Sessions/2026-11-15 My Campaign.md", whether or not it exists.
    public string SessionNotePath(SummarizationOptions options) =>
        Path.Combine(SessionsPath, SanitizeNoteName($"{options.SessionDateTime:yyyy-MM-dd} {options.CampaignName}") + ".md");

    // Without the characters Windows or Obsidian links can't handle.
    public static string SanitizeNoteName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars().Concat("[]#^|").ToHashSet();
        var cleaned = new string(name.Where(c => !invalid.Contains(c)).ToArray()).Trim();
        return cleaned.Length == 0 ? "Session" : cleaned;
    }

    private static IReadOnlyList<string> ReadAliases(string path) => ReadList(path, "aliases:", "alias:");

    // A list from the frontmatter, written inline, as a block, or as a single value.
    private static IReadOnlyList<string> ReadList(string path, params string[] keys)
    {
        try
        {
            using var reader = new StreamReader(path);
            if (reader.ReadLine()?.Trim() != "---") return [];

            var items = new List<string>();
            var inList = false;
            for (var i = 0; i < 200 && reader.ReadLine() is { } line; i++)
            {
                if (line.Trim() == "---") break;

                if (inList)
                {
                    var item = line.TrimStart();
                    if (item.StartsWith('-'))
                    {
                        AddItem(items, item[1..]);
                        continue;
                    }
                    inList = false;
                }

                if (keys.Any(key => line.StartsWith(key, StringComparison.OrdinalIgnoreCase)))
                {
                    var value = line[(line.IndexOf(':') + 1)..].Trim();
                    if (value.Length == 0) inList = true;
                    else if (value.StartsWith('['))
                        foreach (var part in value.Trim('[', ']').Split(',')) AddItem(items, part);
                    else AddItem(items, value);
                }
            }
            return items;
        }
        catch (IOException)
        {
            return [];
        }
    }

    private static void AddItem(List<string> items, string raw)
    {
        var item = raw.Trim().Trim('"', '\'').Trim();
        if (item.Length > 0) items.Add(item);
    }
}
