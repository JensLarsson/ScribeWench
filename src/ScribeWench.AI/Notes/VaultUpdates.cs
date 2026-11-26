using System.Text;
using System.Text.RegularExpressions;
using ScribeWench.AI.Summaries;

namespace ScribeWench.AI.Notes;

// Keeps a game's notes up to date after each session. The model ends its reply with a Vault Updates
// section. New facts go under each note's "From the sessions" heading, where someone was last seen
// goes in a "Last whereabouts" section that each report replaces, and text the session made untrue is
// replaced where it stands, after a backup of the note.
public static class VaultUpdates
{
    public const string UpdatesHeading = "## Vault Updates";
    public const string WhereaboutsHeading = "## Last whereabouts";
    // Stands for this session's note in the new text of a change.
    public const string SessionToken = "{session}";

    // Summary: what is different afterwards, in one sentence.
    public sealed record Change(string Summary, string Find, string Replace);

    public sealed record Update(string Name, bool IsNew, string? Folder, string? Description, IReadOnlyList<string> Facts)
    {
        // One line per place. Empty = no news.
        public IReadOnlyList<string> Whereabouts { get; init; } = [];
        public IReadOnlyList<Change> Changes { get; init; } = [];
    }

    // Created and Updated hold note names.
    public sealed record Result(IReadOnlyList<string> Created, IReadOnlyList<string> Updated)
    {
        public static readonly Result Empty = new([], []);

        // One sentence per change made to what a note says.
        public IReadOnlyList<string> Changes { get; init; } = [];
        // The changes that couldn't be made, each with why.
        public IReadOnlyList<string> NotChanged { get; init; } = [];

        public bool Any => Created.Count > 0 || Updated.Count > 0 || Changes.Count > 0;
    }

    private static readonly Regex SectionHeadingRegex = new(
        @"^\#\#\s*Vault Updates[ \t]*\r?$", RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex NextSectionRegex = new(@"^\#\#\s", RegexOptions.Compiled);

    // "### [[Name]]", "### [[Name]] (new, NPCs)", "### Name (new)"
    private static readonly Regex NoteHeadingRegex = new(
        @"^\#\#\#\s*(?:\[\[)?(?<name>[^\[\]|#()]+?)(?:\|[^\]]*)?(?:\]\])?\s*(?:\(\s*(?<new>new)\b\s*(?:[,:]\s*(?<folder>[^)]*))?\))?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex NothingRegex = new(@"^[*_""]*none\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // "- fact", "* fact", "+ fact"; a line starting "**Vol** ..." is not a bullet.
    private static readonly Regex BulletRegex = new(@"^[-*+]\s+(?<text>.*)$", RegexOptions.Compiled);

    // "Whereabouts: ...", "- **Whereabouts:** ...", "Last whereabouts - ..."
    private static readonly Regex WhereaboutsRegex = new(
        @"^(?:[-*+]\s+)?[*_]*(?:Last\s+)?Whereabouts[*_]*\s*[:—–-]\s*[*_]*\s*(?<text>.+)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // "Changed: ...", "- **Changed:** ..."
    private static readonly Regex ChangedRegex = new(
        @"^(?:[-*+]\s+)?[*_]*Changed?[*_]*\s*:\s*[*_]*\s*(?<text>.+)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // A label with nothing after it, such as "**Last whereabouts**" or "Relationships:"; never a fact.
    private static readonly Regex LabelOnlyRegex = new(@"^(?:[*_]{1,3}[^*_]+[*_]{1,3}:?|[^.!?\[\]]{1,40}:)$", RegexOptions.Compiled);

    // "- [[2026-11-22 Runeterra]]: " or "- 2026-11-22: " at the start of a line ScribeWench wrote.
    private static readonly Regex SessionLabelRegex = new(@"^-\s*(?:\[\[[^\]]+\]\]|\d{4}-\d{2}-\d{2}):\s*", RegexOptions.Compiled);

    // Goes after the notes the model may update. rules is the editable "# Vault updates" section; the
    // layout the code reads back, the folders and what may never be changed are fixed.
    public static string BuildInstructions(IEnumerable<string> folders, string? rules = null)
    {
        var folderList = folders.ToList();
        var sb = new StringBuilder();
        sb.AppendLine("KEEPING THE VAULT UP TO DATE: after the Cast Updates section, end your reply with one last section:");
        sb.AppendLine();
        sb.AppendLine(UpdatesHeading);
        sb.AppendLine("### [[Existing note name]]");
        sb.AppendLine("- [a lasting fact this session established that the note doesn't say yet]");
        sb.AppendLine("Whereabouts: [where they are now or were last seen, and with whom]");
        sb.AppendLine("Changed: [the note's name and what is true of it now, in a few plain words, such as \"[[Marta]] is now dead\"]");
        sb.AppendLine(NoteEdits.FindMarker);
        sb.AppendLine("[text copied exactly from that note, long enough to appear only once in it]");
        sb.AppendLine(NoteEdits.SplitMarker);
        sb.AppendLine("[the text to put in its place]");
        sb.AppendLine(NoteEdits.EndMarker + " END");
        sb.AppendLine("### [[New name]] (new, Folder)");
        sb.AppendLine("[One sentence saying who or what this is.]");
        sb.AppendLine("- [a lasting fact]");
        sb.AppendLine();
        sb.AppendLine("Rules for this section:");
        sb.AppendLine((string.IsNullOrWhiteSpace(rules) ? Prompts.DefaultVaultUpdates : rules).Trim());
        if (folderList.Count > 0)
            sb.AppendLine($"- Put a new note in one of these folders: {string.Join(", ", folderList)}.");
        sb.AppendLine("- Write \"Whereabouts:\" lines only when the rules above ask for whereabouts, and only when this session says something about them. They replace everything in the note's \"Last whereabouts\" section, so give the whole current picture each time, one line per place (for example where they were confirmed seen, and a doubtful claim with who made it). ScribeWench writes that section and its heading itself: don't write a heading or a label line for it, and don't repeat whereabouts as facts.");
        sb.AppendLine($"- Write a \"Changed:\" line with its FIND block only when the rules above ask you to change what a note says, and only for a note whose text is given above. The block puts the text after ======= in place of the FIND text and leaves the rest of the note as it is, so copy the FIND text character for character from the note and keep it to the sentences or lines that change. Each change gets its own \"Changed:\" line and block; the line starts with the note's name as a [[link]] and says only what is true now. Never put lines from a note's \"Last whereabouts\" section in a block; \"Whereabouts:\" lines replace those. Where the new text should point to this session, write {SessionToken}: ScribeWench puts the link there.");
        sb.AppendLine($"- ScribeWench lists the \"Changed:\" sentences itself, under \"{SessionDocuments.ChangesHeading}\" at the end of the note you are writing. Don't write that heading.");
        sb.AppendLine("- Never update session notes or the CAST here.");
        sb.AppendLine("- Write \"None\" there if there is nothing to add.");
        return sb.ToString();
    }

    // Takes the Vault Updates section out of the model's reply.
    public static (string Markdown, IReadOnlyList<Update> Updates) Split(string markdown)
    {
        var heading = SectionHeadingRegex.Match(markdown);
        if (!heading.Success) return (markdown, []);

        var bodyStart = heading.Index + heading.Length;
        var end = SectionEnd(markdown, bodyStart);

        var updates = new List<Update>();
        string? name = null, folder = null, changed = null;
        var isNew = false;
        var description = new List<string>();
        var facts = new List<string>();
        var whereabouts = new List<string>();
        var changes = new List<Change>();
        List<string>? find = null, replace = null;

        void Flush()
        {
            if (name is not null && (facts.Count > 0 || description.Count > 0 || whereabouts.Count > 0 || changes.Count > 0))
                updates.Add(new Update(name, isNew, folder, description.Count > 0 ? string.Join(" ", description) : null, facts.ToList())
                {
                    Whereabouts = whereabouts.Distinct().ToList(),
                    Changes = changes.ToList(),
                });
            name = null; folder = null; changed = null; isNew = false;
            description.Clear(); facts.Clear(); whereabouts.Clear(); changes.Clear();
        }

        foreach (var raw in markdown[bodyStart..end].Split('\n'))
        {
            // Inside a FIND block every line is the note's text, headings and bullets included.
            if (find is not null && replace is null)
            {
                if (raw.Trim() == NoteEdits.SplitMarker) replace = [];
                else find.Add(raw.TrimEnd('\r'));
                continue;
            }
            if (replace is not null)
            {
                if (raw.TrimStart().StartsWith(NoteEdits.EndMarker, StringComparison.Ordinal))
                {
                    if (name is not null)
                        changes.Add(new Change(changed ?? "", string.Join('\n', find!), string.Join('\n', replace)));
                    find = replace = null;
                }
                else replace.Add(raw.TrimEnd('\r'));
                continue;
            }

            var line = raw.Trim();
            if (line.Length == 0) continue;

            if (line.StartsWith("###"))
            {
                Flush();
                var noteHeading = NoteHeadingRegex.Match(line);
                if (!noteHeading.Success) continue;
                name = noteHeading.Groups["name"].Value.Trim();
                isNew = noteHeading.Groups["new"].Success;
                folder = noteHeading.Groups["folder"].Success ? noteHeading.Groups["folder"].Value.Trim() : null;
                continue;
            }
            if (line == NoteEdits.FindMarker)
            {
                find = [];
                continue;
            }
            if (name is null) continue;     // "None", or text before the first note

            if (WhereaboutsRegex.Match(line) is { Success: true } place)
            {
                var text = place.Groups["text"].Value.Trim();
                if (char.IsLower(text[0])) text = char.ToUpperInvariant(text[0]) + text[1..];    // reads as a sentence after the session label
                if (!NothingRegex.IsMatch(text)) whereabouts.Add(text);
                continue;
            }
            if (ChangedRegex.Match(line) is { Success: true } summary)
            {
                // A line of the list at the end of the summary: capitalised, without a full stop.
                changed = summary.Groups["text"].Value.Trim().TrimEnd('.');
                if (changed.Length > 0 && char.IsLower(changed[0])) changed = char.ToUpperInvariant(changed[0]) + changed[1..];
                continue;
            }

            var bullet = BulletRegex.Match(line);
            var content = bullet.Success ? bullet.Groups["text"].Value.Trim() : line;
            if (content.Length == 0 || NothingRegex.IsMatch(content) || LabelOnlyRegex.IsMatch(content)) continue;

            if (bullet.Success) facts.Add(content);
            else description.Add(content);
        }
        Flush();

        var rest = markdown[end..].TrimStart('\r', '\n');
        var cleaned = (markdown[..heading.Index].TrimEnd() + "\n\n" + rest).TrimEnd() + "\n";
        return (cleaned, updates);
    }

    // The next "## " heading. Not one inside a FIND block: the note's text there may hold headings of its own.
    private static int SectionEnd(string markdown, int headingEnd)
    {
        var inBlock = false;
        for (var at = NextLine(markdown, headingEnd); at < markdown.Length; at = NextLine(markdown, at))
        {
            var lineEnd = markdown.IndexOf('\n', at);
            var line = markdown[at..(lineEnd < 0 ? markdown.Length : lineEnd)];

            if (inBlock) inBlock = !line.TrimStart().StartsWith(NoteEdits.EndMarker, StringComparison.Ordinal);
            else if (line.Trim() == NoteEdits.FindMarker) inBlock = true;
            else if (NextSectionRegex.IsMatch(line)) return at;
        }
        return markdown.Length;
    }

    private static int NextLine(string text, int from)
    {
        var lineEnd = text.IndexOf('\n', from);
        return lineEnd < 0 ? text.Length : lineEnd + 1;
    }

    // A fact already in the note is skipped. Session notes, discussion notes, transcripts and the cast
    // note are never touched. backupFolder gets a copy of every note whose text is changed.
    public static Result Apply(ObsidianVault vault, IReadOnlyList<Update> updates, string sessionLabel, string backupFolder)
    {
        if (updates.Count == 0) return Result.Empty;

        var notes = vault.ListNotes().Where(n => !vault.InSessions(n.FullPath)).ToList();
        var folders = Folders(vault);
        var created = new List<string>();
        var updated = new List<string>();
        var changes = new List<string>();
        var notChanged = new List<string>();

        // What the note says is changed first, while its text is still what the model was shown.
        void Bring(string path, string name, Update update)
        {
            ChangeText(vault, path, name, update.Changes, sessionLabel, backupFolder, changes, notChanged);
            if (Write(path, update, sessionLabel)) updated.Add(name);
        }

        foreach (var update in updates)
        {
            var existing = notes.FirstOrDefault(n => n.Name.Equals(update.Name, StringComparison.OrdinalIgnoreCase))
                ?? notes.FirstOrDefault(n => n.Aliases.Any(a => a.Equals(update.Name, StringComparison.OrdinalIgnoreCase)));

            if (existing is not null)
            {
                Bring(existing.FullPath, existing.Name, update);
                continue;
            }

            if (IsSessionLink(update.Name, vault)) continue;

            var name = ObsidianVault.SanitizeNoteName(update.Name);
            // Only into a folder the game already has; otherwise the game's own folder.
            var folder = folders.FirstOrDefault(f => f.Equals(update.Folder?.Trim().Trim('/', '\\'), StringComparison.OrdinalIgnoreCase)) ?? "";
            var path = Path.GetFullPath(Path.Combine(vault.RootPath, folder, name + ".md"));
            if (path.Equals(vault.CastPath, StringComparison.OrdinalIgnoreCase)) continue;

            if (File.Exists(path))
            {
                Bring(path, name, update);
                continue;
            }

            // A note that isn't there has nothing to change, and isn't made for a change alone.
            if (update.Changes.Count > 0) notChanged.Add($"{name}: no such note");
            if (update.Facts.Count == 0 && update.Whereabouts.Count == 0 && update.Description is null) continue;

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var sb = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(update.Description)) sb.Append(update.Description.Trim()).Append("\n\n");
            sb.Append("First appeared in ").Append(sessionLabel).Append(".\n");
            File.WriteAllText(path, sb.ToString());
            Write(path, update, sessionLabel);

            created.Add(name);
            notes.Add(new ObsidianVault.Note(name, folder, [], path));
        }

        return new Result(created, updated) { Changes = changes.Distinct().ToList(), NotChanged = notChanged };
    }

    // Renames the old "Noted by ScribeWench" heading in the game's notes and cast note. Returns the notes renamed.
    public static IReadOnlyList<string> RenameLegacyHeadings(ObsidianVault vault)
    {
        var renamed = new List<string>();
        var paths = vault.ListNotes().Select(n => n.FullPath).Append(vault.CastPath);
        foreach (var path in paths.Where(File.Exists))
        {
            var text = File.ReadAllText(path);
            if (!text.Contains(CastNote.LegacyLearnedHeading, StringComparison.OrdinalIgnoreCase)) continue;
            var updated = CastNote.RenameLegacyHeading(text);
            if (updated == text) continue;      // only mentioned in prose or a comment
            File.WriteAllText(path, updated);
            renamed.Add(Path.GetFileNameWithoutExtension(path));
        }
        return renamed;
    }

    // The game's top-level folders that can hold notes: not sessions, transcripts, attachments or dot-folders.
    public static IReadOnlyList<string> Folders(ObsidianVault vault)
    {
        if (!Directory.Exists(vault.RootPath)) return [];
        string[] reserved = [vault.SessionsPath, vault.TranscriptsPath, vault.AttachmentsPath];
        return Directory.EnumerateDirectories(vault.RootPath)
            .Where(d => !reserved.Contains(Path.GetFullPath(d), StringComparer.OrdinalIgnoreCase))
            .Select(Path.GetFileName)
            .OfType<string>()
            .Where(n => !n.StartsWith('.'))
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // A "### [[2026-11-15 Game]]" heading is an earlier session; those are never created or changed.
    private static bool IsSessionLink(string name, ObsidianVault vault) =>
        Regex.IsMatch(name, @"^\d{4}-\d{2}-\d{2}\b")
        || File.Exists(Path.Combine(vault.SessionsPath, ObsidianVault.SanitizeNoteName(name) + ".md"));

    // Replaces text the session made untrue or out of date. Only an exact, single match is replaced.
    private static void ChangeText(ObsidianVault vault, string path, string name, IReadOnlyList<Change> changes,
        string sessionLabel, string backupFolder, List<string> made, List<string> notMade)
    {
        if (changes.Count == 0) return;

        var original = File.ReadAllText(path);
        var newline = original.Contains("\r\n") ? "\r\n" : "\n";
        var text = original.Replace("\r\n", "\n");
        var summaries = new List<string>();

        foreach (var change in changes)
        {
            var find = change.Find.Replace("\r\n", "\n");
            if (find.Trim().Length == 0) { notMade.Add($"{name}: no text to replace was given"); continue; }

            var (index, count) = NoteEdits.Locate(text, find);
            if (count == 0) { notMade.Add($"{name}: couldn't find \"{NoteEdits.Shorten(find)}\""); continue; }
            if (count > 1) { notMade.Add($"{name}: \"{NoteEdits.Shorten(find)}\" appears more than once"); continue; }

            var replacement = change.Replace.Replace("\r\n", "\n").Replace(SessionToken, sessionLabel);
            var changed = text[..index] + replacement + text[(index + find.Length)..];
            if (changed.Trim().Length == 0) { notMade.Add($"{name}: the change would leave the note empty"); continue; }

            text = changed;
            var summary = change.Summary.Replace(SessionToken, "this session").Trim();
            summaries.Add(summary.Length > 0 ? summary : $"[[{name}]] was changed");
        }
        if (summaries.Count == 0) return;

        // The first copy is the note as it was; a second update of the same note must not replace it.
        var backup = Path.Combine(backupFolder, Path.GetRelativePath(vault.RootPath, path));
        if (!File.Exists(backup))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
            File.WriteAllText(backup, original);
        }

        File.WriteAllText(path, newline == "\n" ? text : text.Replace("\n", "\r\n"));
        made.AddRange(summaries);
    }

    // True when the note changed.
    private static bool Write(string path, Update update, string sessionLabel)
    {
        var added = AddFacts(path, update.Facts, sessionLabel) > 0;
        var moved = SetWhereabouts(path, update.Whereabouts, sessionLabel);
        return added || moved;
    }

    // Replaces the note's "Last whereabouts" section, creating it at the end of the note. Writes nothing
    // when the places are what the section already says.
    private static bool SetWhereabouts(string path, IReadOnlyList<string> places, string sessionLabel)
    {
        if (places.Count == 0) return false;

        var text = CastNote.RenameLegacyHeading(File.ReadAllText(path));
        var newline = text.Contains("\r\n") ? "\r\n" : "\n";
        var lines = text.Replace("\r\n", "\n").TrimEnd('\n').Split('\n').ToList();
        var body = places.Select(p => $"- {sessionLabel}: {p}").ToList();

        var heading = FindHeading(lines, WhereaboutsHeading);
        if (heading < 0)
        {
            lines.AddRange(["", WhereaboutsHeading, ""]);
            lines.AddRange(body);
        }
        else
        {
            var end = SectionEnd(lines, heading);
            var current = lines.Skip(heading + 1).Take(end - heading - 1)
                .Select(l => SessionLabelRegex.Replace(l.Trim(), "").Trim())
                .Where(l => l.Length > 0)
                .ToList();
            if (current.SequenceEqual(places.Select(p => p.Trim()), StringComparer.OrdinalIgnoreCase)) return false;

            lines.RemoveRange(heading + 1, end - heading - 1);
            var replacement = new List<string> { "" };
            replacement.AddRange(body);
            if (heading + 1 < lines.Count) replacement.Add("");     // another section follows
            lines.InsertRange(heading + 1, replacement);
        }

        File.WriteAllText(path, string.Join(newline, lines) + newline);
        return true;
    }

    private static int FindHeading(List<string> lines, string heading) =>
        lines.FindIndex(l => l.Trim().Equals(heading, StringComparison.OrdinalIgnoreCase));

    // The next heading of the same or a higher level.
    private static int SectionEnd(List<string> lines, int heading)
    {
        var end = lines.FindIndex(heading + 1, l => Regex.IsMatch(l, @"^#{1,2}\s"));
        return end < 0 ? lines.Count : end;
    }

    // At the end of the note's "From the sessions" section. Returns how many were new.
    private static int AddFacts(string path, IReadOnlyList<string> facts, string sessionLabel)
    {
        var text = CastNote.RenameLegacyHeading(File.ReadAllText(path));
        var newFacts = facts.Where(f => !text.Contains(f, StringComparison.OrdinalIgnoreCase)).Distinct().ToList();
        if (newFacts.Count == 0) return 0;

        var newline = text.Contains("\r\n") ? "\r\n" : "\n";
        var lines = text.Replace("\r\n", "\n").TrimEnd('\n').Split('\n').ToList();
        var added = newFacts.Select(f => $"- {sessionLabel}: {f}").ToList();

        var heading = FindHeading(lines, CastNote.LearnedHeading);
        var whereabouts = FindHeading(lines, WhereaboutsHeading);
        if (heading < 0 && whereabouts >= 0)
        {
            // Keep "Last whereabouts" last: the new section goes in front of it.
            var at = whereabouts;
            while (at > 0 && lines[at - 1].Trim().Length == 0) at--;
            lines.RemoveRange(at, whereabouts - at);
            var block = new List<string> { "", CastNote.LearnedHeading, "" };
            block.AddRange(added);
            block.Add("");
            lines.InsertRange(at, block);
        }
        else if (heading < 0)
        {
            lines.AddRange(["", CastNote.LearnedHeading, ""]);
            lines.AddRange(added);
        }
        else
        {
            var end = SectionEnd(lines, heading);
            var insertAt = end;
            while (insertAt > heading + 1 && lines[insertAt - 1].Trim().Length == 0) insertAt--;
            if (insertAt == heading + 1) { lines.Insert(insertAt, ""); insertAt++; }
            lines.InsertRange(insertAt, added);
        }

        File.WriteAllText(path, string.Join(newline, lines) + newline);
        return newFacts.Count;
    }
}
