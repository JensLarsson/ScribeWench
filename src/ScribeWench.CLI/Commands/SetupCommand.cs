using System.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using ScribeWench.CLI.Wizard;
using ScribeWench.CLI.Wizard.Environments;
using Spectre.Console;
using Spectre.Console.Cli;

namespace ScribeWench.CLI.Commands;

public sealed class SetupCommandSettings : CommandSettings
{
    [CommandOption("--config <file>")]
    [Description("Set up this config file instead of ~/.scribewench/config.json, for example to try other settings without changing the bot's own.")]
    public string? ConfigPath { get; set; }

    [CommandOption("--dry-run")]
    [Description("Try setup out: nothing is downloaded, installed, started or saved. Downloads and installs are acted out in a few seconds, so every screen can be seen.")]
    public bool DryRun { get; set; }

    [CommandOption("--fresh")]
    [Description("With --dry-run: as on a PC where nothing is set up yet (no saved settings, no Claude Code, no Ollama, no models).")]
    public bool Fresh { get; set; }

    [CommandOption("--ram <GB>")]
    [Description("With --dry-run: pretend the PC has this much RAM.")]
    public double? Ram { get; set; }

    [CommandOption("--card <GB>")]
    [Description("With --dry-run: pretend the PC's graphics card has this much memory (0 for no card).")]
    public double? Card { get; set; }

    public override ValidationResult Validate()
    {
        if (!DryRun && (Fresh || Ram is not null || Card is not null))
            return ValidationResult.Error("--fresh, --ram and --card pretend things about the PC, so they only work together with --dry-run.");
        if (Ram is <= 0 or > 4096)
            return ValidationResult.Error("--ram takes a number of GB, such as 16 or 32.");
        if (Card is < 0 or > 4096)
            return ValidationResult.Error("--card takes a number of GB, such as 8 or 16, or 0 for no card.");
        return ValidationResult.Success();
    }
}

public sealed class SetupCommand : AsyncCommand<SetupCommandSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, SetupCommandSettings settings)
    {
        var services = new ServiceCollection().AddSetupWizard(settings.ConfigPath);
        if (settings.DryRun)
            services.AddDryRun(new DryRunOptions(settings.Fresh, settings.Ram, settings.Card));

        await using var provider = services.BuildServiceProvider();
        return await provider.GetRequiredService<SetupWizard>().RunAsync();
    }
}
