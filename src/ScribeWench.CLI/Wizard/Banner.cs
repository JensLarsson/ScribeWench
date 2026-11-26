using ScribeWench.CLI.Wizard.Environments;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace ScribeWench.CLI.Wizard;

// What setup opens with. The first time it is the scribe herself, with the name in the empty corner
// above her quill. After that, and wherever she can't be drawn, it is the name in large letters.
public sealed class Banner(IAnsiConsole console, ITerminal terminal)
{
    private const string Name = "S c r i b e W e n c h";
    private static readonly string[] Tagline = ["Records your tabletop sessions on Discord", "and writes the session notes."];

    // rowsBelow: what the screen still has to fit under the banner before it waits for an answer.
    public void Show(bool firstRun, int rowsBelow)
    {
        // The picture, cut short at the bottom in a low window. What comes under it has to fit, or
        // the name scrolls off the top; the empty row after it counts too.
        var rows = Math.Min(Mascot.Height, console.Profile.Height - rowsBelow - 1);
        var wideEnough = console.Profile.Width > Picture.Width;
        if (firstRun && wideEnough && rows >= Mascot.Least && terminal.DrawsBraille)
        {
            terminal.Write(new Picture(rows));
            console.WriteLine();
            return;
        }

        console.Write(new FigletText("ScribeWench").Color(Color.Purple));
        console.MarkupLine($"[dim]{string.Join(' ', Tagline)}[/]\n");
    }

    // The scribe's first rows, written as they are: nothing is wrapped or trimmed.
    private sealed class Picture(int rows) : Renderable
    {
        public static int Width => Mascot.Corner + Tagline.Max(line => line.Length);

        protected override Measurement Measure(RenderOptions options, int maxWidth) => new(Width, Width);

        protected override IEnumerable<Segment> Render(RenderOptions options, int maxWidth)
        {
            for (var row = 0; row < rows; row++)
            {
                // The name on her third row, and what the program does from the fifth.
                var caption = row switch
                {
                    2 => new Segment(Name, new Style(Color.Purple)),
                    4 or 5 => new Segment(Tagline[row - 4], new Style(decoration: Decoration.Dim)),
                    _ => null,
                };
                foreach (var segment in Mascot.Row(row, caption is null ? 0 : Mascot.Corner))
                    yield return segment;
                if (caption is not null) yield return caption;
                yield return Segment.LineBreak;
            }
        }
    }
}
