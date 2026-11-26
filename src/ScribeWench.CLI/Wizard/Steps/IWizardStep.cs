using ScribeWench.Core.Models;

namespace ScribeWench.CLI.Wizard.Steps;

/// <summary>
/// Strategy for one part of setup: a numbered step on a first run, and an entry in the menu
/// afterwards. The wizard runs them in the order they are registered.
/// </summary>
public interface IWizardStep
{
    /// <summary>Its heading as a numbered step: "The Discord bot".</summary>
    string Title { get; }

    /// <summary>Its entry in the menu: "Change the Discord bot".</summary>
    string MenuLabel { get; }

    /// <summary>Asks its questions and writes the answers into the settings. fromMenu: picked from the menu, not walked through on a first run.</summary>
    Task RunAsync(AppConfig config, bool fromMenu, CancellationToken ct);
}
