using System.Text;
using System.Text.RegularExpressions;

namespace ScribeWench.AI.Summaries;

// The rules a summary is written by, kept as one Markdown file with four sections. Only the rules
// are editable: the layouts the code reads back are added by the code, so no edit can stop the
// replies from being understood.
public sealed record Instructions(string SessionNote, string DiscussionNote, string CastUpdates, string VaultUpdates)
{
    public const string SessionNoteSection = "Session note";
    public const string DiscussionNoteSection = "Discussion note";
    public const string CastUpdatesSection = "Cast updates";
    public const string VaultUpdatesSection = "Vault updates";
    public static readonly IReadOnlyList<string> SectionNames =
        [SessionNoteSection, DiscussionNoteSection, CastUpdatesSection, VaultUpdatesSection];

    // Far more than any sensible set of rules.
    public const int MaxFileBytes = 100_000;

    public static Instructions Default { get; } =
        new(Prompts.DefaultSessionNote, Prompts.DefaultDiscussionNote, Prompts.DefaultCastUpdates, Prompts.DefaultVaultUpdates);

    // Starts every instructions file ScribeWench writes. A comment, so it isn't sent to the model.
    public const string FileHeader = """
        <!--
        ScribeWench instructions: the rules session summaries are written by.

        There are four sections, each starting with one of these headings, written exactly like this:
          # Session note       what the session note says and how it's laid out
          # Discussion note    what to keep when the group talks about the campaign without playing
          # Cast updates       what to report about who plays whom (added to the Cast note)
          # Vault updates      which lasting facts to add to the game's other notes, and when to
                               change what a note already says
        Keep them all. Inside a section, write whatever rules you like. A file from before
        "# Discussion note" existed still works: it gets the built-in rules for it.

        ScribeWench adds the fixed parts itself: how to reply when a recording isn't a game session,
        and the exact layout of the Cast Updates and Vault Updates sections it reads back. So don't use
        "## Cast Updates" or "## Vault Updates" as headings yourself.

        Each game can have its own copy. In Discord:
          instructions-show <game>    sends the game's instructions as a file
          instructions-edit <game>    with the edited file attached, replaces them
          instructions-reset <game>   goes back to the defaults
        The defaults are the summary-prompt.md file (ai.promptFile in config.json); delete that file
        to get the built-in defaults back.

        Text inside these comment markers is not sent to the model.
        -->


        """;

    public IEnumerable<(string Name, string Text)> Sections =>
    [
        (SessionNoteSection, SessionNote), (DiscussionNoteSection, DiscussionNote),
        (CastUpdatesSection, CastUpdates), (VaultUpdatesSection, VaultUpdates),
    ];

    public string ToFile()
    {
        var sb = new StringBuilder(FileHeader);
        foreach (var (name, text) in Sections)
            sb.Append("# ").Append(name).Append("\n\n").Append(text.Trim()).Append("\n\n");
        return sb.ToString().TrimEnd() + "\n";
    }

    private static readonly Regex CommentRegex = new(@"<!--.*?-->", RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex TopHeadingRegex = new(@"^#[ \t]+(?<name>.*?)[ \t#:]*$", RegexOptions.Compiled);
    private static readonly Regex ReservedHeadingRegex = new(@"^\#\#[ \t]*(?<name>Cast Updates|Vault Updates)[ \t]*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex FenceRegex = new(@"^(?<fence>`{3,}|~{3,})", RegexOptions.Compiled);

    public static string StripComments(string text) => CommentRegex.Replace(text, "").Trim();

    // Instructions is set only when the file has its sections and nothing wrong with it. The discussion
    // note section came later, so a file without it gets the built-in one.
    public sealed record Parsed(Instructions? Instructions, IReadOnlyDictionary<string, string> Sections, IReadOnlyList<string> Problems);

    public static Parsed Parse(string text)
    {
        var problems = new List<string>();
        text = text.Replace("\r\n", "\n");

        var withoutComments = CommentRegex.Replace(text, "");
        if (withoutComments.Contains("<!--", StringComparison.Ordinal))
            problems.Add("A `<!--` comment is never closed with `-->`.");

        var sections = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? current = null;
        var anyHeading = false;
        var body = new List<string>();
        var before = new StringBuilder();
        string? fence = null;

        void Flush()
        {
            if (current is null) return;
            var sectionText = string.Join("\n", body).Trim();
            if (sections.ContainsKey(current))
                problems.Add($"`# {current}` appears twice.");
            else
                sections[current] = sectionText;
            body.Clear();
        }

        foreach (var line in withoutComments.Split('\n'))
        {
            var fenceMatch = FenceRegex.Match(line.TrimStart());
            if (fence is null && fenceMatch.Success) fence = fenceMatch.Groups["fence"].Value[..3];
            else if (fence is not null && line.TrimStart().StartsWith(fence, StringComparison.Ordinal)) fence = null;
            else if (fence is null && TopHeadingRegex.Match(line) is { Success: true } heading)
            {
                Flush();
                anyHeading = true;
                var name = heading.Groups["name"].Value.Trim();
                current = SectionNames.FirstOrDefault(n => n.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (current is null)
                    problems.Add($"Unknown section `# {name}`. The sections are {SectionList}.");
                continue;
            }
            else if (fence is null && current is not null && ReservedHeadingRegex.Match(line) is { Success: true } reserved)
            {
                problems.Add($"`# {current}` uses the heading `## {reserved.Groups["name"].Value}`, which ScribeWench adds itself. Rename it.");
            }

            if (current is not null) body.Add(line);
            else if (!anyHeading) before.AppendLine(line);     // under an unknown heading: already reported
        }
        Flush();

        if (fence is not null)
            problems.Add("A code block is opened but never closed.");
        if (before.ToString().Trim().Length > 0)
            problems.Add("There is text before the first section, which wouldn't be used. Move it into a section, or inside a `<!-- -->` comment.");

        foreach (var name in SectionNames)
        {
            if (!sections.TryGetValue(name, out var sectionText))
            {
                if (name != DiscussionNoteSection) problems.Add($"Missing section `# {name}`.");
            }
            else if (sectionText.Length == 0) problems.Add($"`# {name}` is empty.");
        }

        Instructions? instructions = problems.Count == 0
            ? new Instructions(
                sections[SessionNoteSection],
                sections.GetValueOrDefault(DiscussionNoteSection, Prompts.DefaultDiscussionNote),
                sections[CastUpdatesSection],
                sections[VaultUpdatesSection])
            : null;
        return new Parsed(instructions, sections, problems.Distinct().ToList());
    }

    // What an uploaded file gets before it can replace a game's instructions.
    public sealed record Review(Instructions? Instructions, string Text, IReadOnlyList<string> Problems)
    {
        public bool Ok => Instructions is not null;
    }

    public static Review ReviewUpload(string fileName, byte[] bytes)
    {
        if (!Path.GetExtension(fileName).Equals(".md", StringComparison.OrdinalIgnoreCase))
            return Rejected("It isn't a `.md` file.");
        if (bytes.Length > MaxFileBytes)
            return Rejected($"It's {bytes.Length / 1000:N0} KB; the most is {MaxFileBytes / 1000} KB.");

        string text;
        try
        {
            text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return Rejected("It isn't UTF-8 text. Save it as UTF-8 and try again.");
        }
        text = text.TrimStart('\uFEFF');     // byte order mark

        if (text.Any(c => char.IsControl(c) && c is not ('\n' or '\r' or '\t')))
            return Rejected("It looks like a binary file, not text.");
        if (StripComments(text).Length == 0)
            return Rejected("It has no instructions outside comments.");

        var parsed = Parse(text);
        return new Review(parsed.Instructions, text, parsed.Problems);

        static Review Rejected(string problem) => new(null, "", [problem]);
    }

    // For a file that is partly filled in or in the old one-prompt format: sections it has replace
    // those of the fallback, and a file with no sections at all is taken as the session note rules.
    public static Instructions ParseLenient(string text, Instructions fallback, out IReadOnlyList<string> problems)
    {
        var parsed = Parse(text);
        problems = parsed.Problems;
        if (parsed.Instructions is { } complete) return complete;

        if (parsed.Sections.Count == 0)
        {
            problems = [];
            return fallback with { SessionNote = StripComments(text) };
        }

        string Pick(string name, string fallbackText) =>
            parsed.Sections.TryGetValue(name, out var t) && t.Length > 0 ? t : fallbackText;
        return new Instructions(
            Pick(SessionNoteSection, fallback.SessionNote),
            Pick(DiscussionNoteSection, fallback.DiscussionNote),
            Pick(CastUpdatesSection, fallback.CastUpdates),
            Pick(VaultUpdatesSection, fallback.VaultUpdates));
    }

    public sealed record SectionChange(string Section, int Added, int Removed)
    {
        public bool Changed => Added > 0 || Removed > 0;
    }

    // Lines added and removed per section, ignoring blank lines and indentation.
    public static IReadOnlyList<SectionChange> Compare(Instructions before, Instructions after) =>
        before.Sections.Zip(after.Sections, (b, a) =>
        {
            var oldLines = Lines(b.Text);
            var newLines = Lines(a.Text);
            return new SectionChange(b.Name, CountMissing(newLines, oldLines), CountMissing(oldLines, newLines));
        }).ToList();

    private static List<string> Lines(string text) =>
        text.Replace("\r\n", "\n").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();

    // Counting repeats.
    private static int CountMissing(List<string> lines, List<string> other)
    {
        var remaining = other.GroupBy(l => l, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var missing = 0;
        foreach (var line in lines)
        {
            if (remaining.TryGetValue(line, out var n) && n > 0) remaining[line] = n - 1;
            else missing++;
        }
        return missing;
    }

    private static string SectionList => string.Join(", ", SectionNames.Select(n => $"`# {n}`"));
}
