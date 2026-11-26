using Spectre.Console.Rendering;

namespace ScribeWench.CLI.Wizard.Environments;

/// <summary>
/// Strategy for the terminal setup is shown in, as far as the picture on its first screen goes. The
/// picture is made of braille characters, which not every terminal has a font for and not every
/// console encoding can carry. The real one asks Windows, the tests make it up.
/// </summary>
public interface ITerminal
{
    /// <summary>Whether braille characters come out as dots here, and not as boxes or question marks.</summary>
    bool DrawsBraille { get; }

    /// <summary>Writes something with braille in it.</summary>
    void Write(IRenderable picture);
}
