namespace ScribeWench.Bot;

public static class DiscordText
{
    // An inline code span. Markdown escapes don't apply inside one (a sanitized "Vol. 2" would show
    // as "Vol\. 2"), so only backticks, which would end the span, are replaced.
    public static string Code(string text) => "`" + text.Replace('`', '\'') + "`";

    // Discord takes 2,000 characters a message; this cuts between lines.
    public static List<string> SplitIntoChunks(string text, int maxSize = 1900)
    {
        var chunks = new List<string>();
        var current = new System.Text.StringBuilder();
        foreach (var line in text.Split('\n'))
        {
            if (current.Length + line.Length + 1 > maxSize && current.Length > 0)
            {
                chunks.Add(current.ToString().TrimEnd());
                current.Clear();
            }
            current.AppendLine(line);
        }
        if (current.Length > 0) chunks.Add(current.ToString().TrimEnd());
        return chunks;
    }
}
