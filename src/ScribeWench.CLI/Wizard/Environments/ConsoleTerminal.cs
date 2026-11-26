using System.Runtime.InteropServices;
using System.Text;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace ScribeWench.CLI.Wizard.Environments;

// The console window setup really runs in.
public sealed class ConsoleTerminal(IAnsiConsole console) : ITerminal
{
    // On Windows the window decides: Windows Terminal and its like borrow the dots from another
    // font, the console window of old shows boxes. Anywhere else the encoding decides.
    public bool DrawsBraille => console.Profile.Out.IsTerminal
        && (OperatingSystem.IsWindows() ? !InClassicWindow() : console.Profile.Capabilities.Unicode);

    public void Write(IRenderable picture)
    {
        if (console.Profile.Capabilities.Unicode || !OperatingSystem.IsWindows())
        {
            console.Write(picture);
            return;
        }

        // A Windows console on a code page (437, 850) turns braille into question marks. As UTF-16
        // it takes any character, and no code page is changed. Spectre keeps the writer it started
        // with, so the picture gets a console of its own, for this call only: it must not outlive
        // the switch.
        var codePage = Console.OutputEncoding;
        Console.OutputEncoding = Encoding.Unicode;
        try
        {
            AnsiConsole.Create(new AnsiConsoleSettings()).Write(picture);
        }
        finally
        {
            Console.OutputEncoding = codePage;
        }
    }

    // Every other terminal talks to the program through a pseudoconsole, whose window is of another
    // class (Windows 11) or hidden (Windows 10).
    private static bool InClassicWindow()
    {
        var window = GetConsoleWindow();
        var name = new StringBuilder(64);
        GetClassName(window, name, name.Capacity);
        return name.ToString() == "ConsoleWindowClass" && IsWindowVisible(window);
    }

    [DllImport("kernel32.dll")]
    private static extern nint GetConsoleWindow();

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(nint window);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(nint window, StringBuilder name, int size);
}
