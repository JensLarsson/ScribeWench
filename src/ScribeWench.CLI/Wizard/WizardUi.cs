using Spectre.Console;
using Spectre.Console.Rendering;

namespace ScribeWench.CLI.Wizard;

// The pieces every part of setup asks and shows things with.
public sealed class WizardUi(IAnsiConsole console)
{
    public void Step(int number, int of, string title)
    {
        console.WriteLine();
        console.Write(new Rule($"[bold]Step {number} of {of}: {title}[/]").LeftJustified().RuleStyle("grey"));
    }

    // A list to pick one from with the arrow keys. The cursor starts on the first entry, so that is
    // where the suggested answer goes. Labels are markup: the entry itself, three spaces, then any
    // small print about it.
    public T Choose<T>(string title, params (T Value, string Label)[] choices) => Choose(title, search: false, choices);

    public T Choose<T>(string title, bool search, params (T Value, string Label)[] choices)
    {
        var prompt = new SelectionPrompt<int>()
            .Title(title)
            .PageSize(Math.Clamp(choices.Length, 3, 15))
            .HighlightStyle("green")
            .MoreChoicesText("[dim](more below: arrow down, or type to search)[/]")
            .UseConverter(index => choices[index].Label)
            .AddChoices(Enumerable.Range(0, choices.Length));
        if (search) prompt.EnableSearch();

        var chosen = choices[console.Prompt(prompt)];
        // The list is wiped once an entry is picked; leave the answer behind, without its small print.
        console.MarkupLine($"{title} [green]{chosen.Label.Trim().Split("   ")[0]}[/]");
        return chosen.Value;
    }

    // A key or token, typed unseen. With one saved already, keeping it is the default, and the
    // saved one is never printed.
    public string AskSecret(string label, string? saved, bool allowEmpty = false)
    {
        if (!string.IsNullOrEmpty(saved) && console.Confirm($"Keep the saved {label}?", defaultValue: true))
            return saved;

        var prompt = new TextPrompt<string>($"Paste the {label}:").Secret();
        if (allowEmpty) prompt.AllowEmpty();
        return console.Prompt(prompt).Trim();
    }

    // How many rows something takes on a console as wide as this one. It is written to a console
    // nobody sees, made for this call only: the real one would show it.
    public int RowsOf(Action<IAnsiConsole> write)
    {
        var text = new StringWriter();
        var unseen = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Interactive = InteractionSupport.No,
            Out = new AnsiConsoleOutput(text),
        });
        unseen.Profile.Width = console.Profile.Width;
        write(unseen);

        var written = text.ToString();
        return written.Count(c => c == '\n') + (written.Length > 0 && !written.EndsWith('\n') ? 1 : 0);
    }

    // The rows a list from Choose takes while it waits. Spectre lays it out as the title, an empty
    // row, and each answer behind its pointer in a column one short of the console's width, where a
    // long answer goes on to a second row.
    public int RowsOf<T>(string title, params (T Value, string Label)[] choices) => RowsOf(to =>
    {
        var answers = new Grid().AddColumn(new GridColumn().Padding(0, 0, 1, 0).NoWrap()).AddEmptyRow();
        foreach (var (_, label) in choices) answers.AddRow(new Markup("> " + label));
        to.Write(new Rows(new Markup(title), answers));
    });

    public static string Size(long bytes) => bytes >= 1_000_000_000 ? $"{bytes / 1e9:0.0} GB" : $"{bytes / 1e6:0} MB";

    // The columns of a download's progress bar.
    public static ProgressColumn[] DownloadColumns() =>
        [new TaskDescriptionColumn(), new ProgressBarColumn(), new PercentageColumn(), new DownloadedSizeColumn(), new TransferSpeedColumn(), new RemainingTimeColumn()];

    // In the GB of a thousand MB that the questions and Ollama's own pages use. Spectre's own
    // column counts in units of 1024, which makes an "18 GB" download end at 16.5.
    private sealed class DownloadedSizeColumn : ProgressColumn
    {
        public override IRenderable Render(RenderOptions options, ProgressTask task, TimeSpan deltaTime)
        {
            if (task.IsIndeterminate) return Text.Empty;
            return task.IsFinished
                ? new Markup($"[green]{Size((long)task.MaxValue)}[/]")
                : new Markup($"{Size((long)task.Value)} of {Size((long)task.MaxValue)}");
        }
    }
}
