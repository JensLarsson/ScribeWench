using ScribeWench.CLI.Wizard.Environments;
using Spectre.Console;

namespace ScribeWench.CLI.Wizard;

// What one run of setup has learned so far, shared by its steps.
public sealed class SetupSession(ISetupEnvironment environment, IAnsiConsole console)
{
    private MachineInfo? _machine;

    // Nothing was saved before this run, so nothing on screen should be called "current".
    public bool FirstRun { get; set; }

    // The bot's name as Discord gave it, for the review.
    public string? BotName { get; set; }

    // What is wrong with the saved token, when Discord says something is.
    public string? BotProblem { get; set; }

    // The machine as detected, or as the user describes it when detection finds nothing.
    public MachineInfo Machine()
    {
        if (_machine is not null) return _machine;

        var machine = environment.DetectMachine();
        // Every PC has some graphics adapter, so an empty list means the question couldn't be asked.
        if (machine.Cards.Count == 0)
        {
            var gb = console.Prompt(
                new TextPrompt<double>("Couldn't see this PC's graphics card. How many GB of memory does it have? [dim](0 for none)[/]")
                    .DefaultValue(0)
                    .Validate(v => v is >= 0 and <= 512 ? ValidationResult.Success() : ValidationResult.Error("A number of GB, such as 8 or 16")));
            if (gb > 0)
                machine = machine with { Cards = [new GraphicsCard("graphics card", gb)] };
        }
        return _machine = machine;
    }
}
