using System.Text;
using System.Text.RegularExpressions;
using ScribeWench.AI.Summaries;

namespace ScribeWench.AI.Notes;

// A game's cast note: who plays whom and who runs the game. People edit it by hand, so what the
// model learns is appended under its own heading and hand-written lines are never changed.
public static class CastNote
{
    public const string UpdatesHeading = "## Cast Updates";
    // Heads the lasting facts that came up in play, in the cast note and the game's other notes.
    public const string LearnedHeading = "## From the sessions";
    // What it used to be called; renamed whenever a note with it is written.
    public const string LegacyLearnedHeading = "## Noted by ScribeWench";

    private static readonly Regex LegacyHeadingRegex = new(
        @"^[ \t]*\#\#[ \t]+Noted by ScribeWench[ \t]*(?=\r?$)", RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static string RenameLegacyHeading(string text) => LegacyHeadingRegex.Replace(text, LearnedHeading);

    // Whisper only keeps a short prompt (about 220 tokens), and fantasy names take several tokens each.
    private const int MaxHintLength = 500;

    private static readonly Regex UpdatesRegex = new(
        @"^\#\#\s*Cast Updates[ \t]*\r?$(?<body>.*?)(?=^\#\#\s|\z)",
        RegexOptions.Multiline | RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex FrontmatterRegex = new(@"\A---\r?\n.*?\r?\n---\r?\n", RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex NothingRegex = new(@"^none\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Written when a game is created. The comment in it is not sent to the model.
    public const string Template = """
        # Cast

        <!--
        Who plays whom in this game. ScribeWench sends this note to the AI with every session, and
        uses the names in it to help the transcriber spell them. Write it however you like, for example:

        - **Erik**: DM
        - **Anna**: plays Thorin and Elara

        Use the names people have in Discord. What ScribeWench works out by itself is added at the
        bottom under "From the sessions"; correct or delete those lines freely.
        -->

        """;

    // The note as sent to the model: no frontmatter, no comments. Null when missing or empty.
    public static string? Read(string path)
    {
        if (!File.Exists(path)) return null;

        var text = Instructions.StripComments(FrontmatterRegex.Replace(File.ReadAllText(path), ""));
        // A note holding only its title and headings says nothing yet.
        var hasContent = text.Split('\n').Any(l => l.Trim().Length > 0 && !l.TrimStart().StartsWith('#'));
        return hasContent ? text : null;
    }

    // rules is the editable "# Cast updates" section; the layout the code reads back is fixed.
    public static string BuildContext(string? cast, string? rules = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine("CAST (who plays whom, kept by the group; the names in bold are the speaker names in the transcript):");
        sb.AppendLine(string.IsNullOrWhiteSpace(cast) ? "(nothing written yet)" : cast.Trim());
        sb.AppendLine();
        sb.AppendLine("In addition to the session note, end your reply with one more section:");
        sb.AppendLine();
        sb.AppendLine(UpdatesHeading);
        sb.AppendLine("- **[speaker name exactly as in the transcript]** [one new fact about the cast]");
        sb.AppendLine();
        sb.AppendLine("Rules for this section:");
        sb.AppendLine((string.IsNullOrWhiteSpace(rules) ? Prompts.DefaultCastUpdates : rules).Trim());
        sb.AppendLine("- One fact per line.");
        sb.AppendLine("- Write \"None\" there if there is nothing new.");
        return sb.ToString();
    }

    // Takes the Cast Updates section out of the model's reply.
    public static (string Markdown, IReadOnlyList<string> Updates) SplitUpdates(string markdown)
    {
        var match = UpdatesRegex.Match(markdown);
        if (!match.Success) return (markdown, []);

        var updates = match.Groups["body"].Value
            .Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.StartsWith('-') || l.StartsWith('*'))
            .Select(l => l.TrimStart('-', ' ').Trim())
            .Where(l => l.Length > 0 && !NothingRegex.IsMatch(l.TrimStart('*', '_', '"')))
            .ToList();

        var rest = markdown[(match.Index + match.Length)..].TrimStart('\r', '\n');
        var cleaned = (markdown[..match.Index].TrimEnd() + "\n\n" + rest).TrimEnd() + "\n";
        return (cleaned, updates);
    }

    // Dated lines under "From the sessions", creating the note or the heading if needed.
    public static void Append(string path, IReadOnlyList<string> updates, DateTime sessionDate)
    {
        if (updates.Count == 0) return;

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var text = RenameLegacyHeading(File.Exists(path) ? File.ReadAllText(path) : Template);

        var sb = new StringBuilder(text.TrimEnd());
        sb.AppendLine();
        if (!text.Contains(LearnedHeading, StringComparison.OrdinalIgnoreCase))
        {
            sb.AppendLine();
            sb.AppendLine(LearnedHeading);
            sb.AppendLine();
        }
        foreach (var update in updates)
            sb.AppendLine($"- {sessionDate:yyyy-MM-dd}: {update}");

        File.WriteAllText(path, sb.ToString());
    }

    // The cast as plain text for Whisper: names without Markdown, cut to what Whisper keeps.
    public static string? ToWhisperHint(string? cast)
    {
        if (string.IsNullOrWhiteSpace(cast)) return null;

        var text = SessionDocuments.StripWikiLinks(cast);
        text = Regex.Replace(text, @"^\s*#+\s.*$", "", RegexOptions.Multiline);       // headings
        text = Regex.Replace(text, @"\b\d{4}-\d{2}-\d{2}:\s*", "");                   // dates on learned lines
        text = Regex.Replace(text, @"[*_`>\[\]{}]", "");
        text = Regex.Replace(text, @"^\s*[-•]\s*", "", RegexOptions.Multiline);
        text = Regex.Replace(text, @"[\s.]*\r?\n\s*", ". ");
        text = Regex.Replace(text, @"\s+", " ").Trim(' ', '.');
        if (text.Length == 0) return null;

        if (text.Length > MaxHintLength)
        {
            var cut = text.LastIndexOf(' ', MaxHintLength);
            text = text[..(cut > 0 ? cut : MaxHintLength)].TrimEnd(' ', '.', ',');
        }
        return text + ".";
    }
}
