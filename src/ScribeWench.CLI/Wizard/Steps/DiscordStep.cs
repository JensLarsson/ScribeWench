using ScribeWench.CLI.Wizard.Environments;
using ScribeWench.Core.Models;
using Spectre.Console;

namespace ScribeWench.CLI.Wizard.Steps;

// The Discord bot. The user makes the bot on Discord's site and pastes its token; setup then asks
// Discord itself whether the token works, whether the bot may read messages, and whether it has
// been added to a server, instead of taking the user's word for each.
public sealed class DiscordStep(DiscordApi api, ISetupEnvironment environment, SetupSession session, WizardUi ui, IAnsiConsole console) : IWizardStep
{
    public string Title => "The Discord bot";
    public string MenuLabel => "Change the Discord bot";

    // So the review can name the bot, or say that its token has stopped working.
    public async Task LookUpSavedBotAsync(DiscordConfig discord, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(discord.BotToken)) return;

        var (answer, bot) = await api.GetBotAsync(discord.BotToken, ct);
        session.BotName = bot?.Name;
        session.BotProblem = answer == DiscordAnswer.Rejected ? "Discord no longer accepts the saved token" : null;
    }

    public async Task RunAsync(AppConfig config, bool fromMenu, CancellationToken ct)
    {
        var discord = config.Discord;
        DiscordBot? bot = null;
        var askForToken = true;

        // With no token yet, how to get one. With one saved, whether it still works.
        if (string.IsNullOrEmpty(discord.BotToken))
        {
            ShowHowToMakeABot();
        }
        else
        {
            var (answer, current) = await api.GetBotAsync(discord.BotToken, ct);
            switch (answer)
            {
                case DiscordAnswer.Ok:
                    bot = current;
                    console.MarkupLine($"The saved token belongs to the bot [green]{Markup.Escape(current!.Name)}[/].");
                    askForToken = console.Confirm("Use a different bot?", defaultValue: false);
                    if (askForToken) bot = null;
                    break;
                case DiscordAnswer.Rejected:
                    console.MarkupLine("[yellow]Discord no longer accepts the saved token.[/] Make a new one: open the bot's page at");
                    console.MarkupLine($"[link]{DiscordApi.ApplicationsUrl}[/], go to [bold]Bot[/] and press [bold]Reset Token[/].");
                    break;
                default:
                    console.MarkupLine("[yellow]Couldn't reach Discord to check the saved token.[/]");
                    askForToken = console.Confirm("Enter a different token anyway?", defaultValue: false);
                    break;
            }
        }

        // The token, until Discord accepts one or the user leaves it for later.
        var changed = false;
        while (askForToken)
        {
            var token = console.Prompt(
                new TextPrompt<string>("Paste the bot token [dim](or just press Enter to do this later)[/]:")
                    .Secret()
                    .AllowEmpty())
                .Trim();

            if (token.Length == 0)
            {
                console.MarkupLine(string.IsNullOrEmpty(discord.BotToken)
                    ? "[yellow]No bot yet.[/] Everything else can be set up now. Run [bold]scribewench setup[/] again when you have the token."
                    : "[dim]The saved token stays as it is.[/]");
                break;
            }

            var (answer, found) = await api.GetBotAsync(token, ct);
            if (answer == DiscordAnswer.Ok)
            {
                discord.BotToken = token;
                bot = found;
                changed = true;
                console.MarkupLine($"[green]That works.[/] The bot is called [green]{Markup.Escape(found!.Name)}[/].");
                break;
            }

            if (answer == DiscordAnswer.Rejected)
            {
                console.MarkupLine("[red]Discord doesn't accept that token.[/] Copy it again with the Copy button next to it. It is only shown");
                console.MarkupLine("once: if it's gone, press [bold]Reset Token[/] for a new one. [dim](The token is not the Application ID or the Public Key.)[/]");
                continue;
            }

            console.MarkupLine("[yellow]Couldn't reach Discord to check the token.[/] Is this PC online?");
            if (console.Confirm("Save the token without checking it?", defaultValue: false))
            {
                discord.BotToken = token;
                changed = true;
                session.BotName = null;
                break;
            }
        }

        // What a working bot still needs: to read messages, and to be in a server.
        if (changed || bot is not null) session.BotProblem = null;
        if (bot is not null)
        {
            session.BotName = bot.Name;
            var application = await api.GetApplicationAsync(discord.BotToken, ct);
            var applicationId = application is { Id.Length: > 0 } ? application.Id : bot.Id;

            await CheckMessageContentAsync(discord.BotToken, application, applicationId, ct);
            await CheckServersAsync(discord.BotToken, applicationId, fromMenu, ct);
        }

        if (fromMenu)
            discord.CommandPrefix = console.Prompt(
                new TextPrompt<string>("Commands start with:")
                    .DefaultValue(discord.CommandPrefix)
                    .Validate(text => string.IsNullOrWhiteSpace(text) ? ValidationResult.Error("It can't be empty") : ValidationResult.Success()))
                .Trim();
    }

    private void ShowHowToMakeABot()
    {
        console.MarkupLine("The bot is yours: you make it on Discord's site (free, two minutes) and paste its token here.\n");

        var steps = new Grid().AddColumn(new GridColumn().NoWrap()).AddColumn();
        steps.AddRow("[bold grey]1.[/]", $"Open [link]{DiscordApi.ApplicationsUrl}[/] and sign in.");
        steps.AddRow("[bold grey]2.[/]", "Make a new application: give it a name (ScribeWench, say) and press [bold]Create[/].");
        steps.AddRow("[bold grey]3.[/]", "Open its [bold]Bot[/] page from the menu on the left.");
        steps.AddRow("[bold grey]4.[/]", "Under [bold]Privileged Gateway Intents[/], switch on [bold]Message Content Intent[/] and save.\n[dim]Without it the bot can't read commands. It needs neither of the other two.[/]");
        steps.AddRow("[bold grey]5.[/]", "Under [bold]Token[/], press [bold]Reset Token[/] and copy the token it shows.\n[dim]Discord may ask for your password or 2FA code. The token is shown once.[/]");
        console.Write(steps);
        console.WriteLine();

        if (console.Confirm("Open that page in your browser now?", defaultValue: true) && !environment.OpenInBrowser(DiscordApi.ApplicationsUrl))
            console.MarkupLine($"[dim]Couldn't open a browser. Go to {DiscordApi.ApplicationsUrl} yourself.[/]");
    }

    // The bot reads commands as ordinary messages, which Discord only lets it do with this switch on.
    private async Task CheckMessageContentAsync(string token, DiscordApplication? application, string applicationId, CancellationToken ct)
    {
        while (true)
        {
            // Discord didn't say: nothing to report.
            if (application?.MessageContentIntent is null) return;
            if (application.MessageContentIntent == true)
            {
                console.MarkupLine("[green]Message Content Intent is on.[/]");
                return;
            }

            var page = DiscordApi.BotPageUrl(applicationId);
            console.MarkupLine("[yellow]Message Content Intent is off[/], so the bot can't read commands. Switch it on under");
            console.MarkupLine($"[bold]Privileged Gateway Intents[/] and save: [link]{page}[/]");

            var next = ui.Choose("Then:",
                ("open", "Open that page in my browser"),
                ("check", "I've switched it on: check again"),
                ("skip", "Carry on without checking"));
            if (next == "skip") return;
            if (next == "open" && !environment.OpenInBrowser(page))
                console.MarkupLine("[dim]Couldn't open a browser. Copy the link above.[/]");
            if (next == "open")
                console.Confirm("Switched on and saved?", defaultValue: true);

            application = await api.GetApplicationAsync(token, ct);
        }
    }

    // A bot does nothing until it is in a server. Its owner adds it with a link that carries the permissions it needs.
    private async Task CheckServersAsync(string token, string applicationId, bool fromMenu, CancellationToken ct)
    {
        var invite = DiscordApi.InviteUrl(applicationId);

        var servers = await api.GetServerNamesAsync(token, ct);
        if (servers is { Count: > 0 })
        {
            console.MarkupLine($"[green]The bot is in:[/] {Markup.Escape(string.Join(", ", servers))}");
            if (fromMenu) console.MarkupLine($"[dim]To add it to another server:[/] [link]{invite}[/]");
            return;
        }

        console.MarkupLine("The bot isn't in a server yet. This link adds it, with the permissions it needs already ticked:");
        console.MarkupLine($"[link]{invite}[/]");
        console.MarkupLine("[dim]Pick your server and press Authorise. You need the Manage Server permission there.[/]");

        var opened = false;
        while (true)
        {
            var open = ("open", opened ? "Open the link again" : "Open the link in my browser");
            var check = ("check", "I've added it: check");
            var skip = ("skip", "Do this later");

            var next = opened ? ui.Choose("Then:", check, open, skip) : ui.Choose("Then:", open, check, skip);
            if (next == "skip")
            {
                console.MarkupLine("[dim]The link stays the same, and `scribewench setup` shows it again.[/]");
                return;
            }
            if (next == "open")
            {
                opened = true;
                if (!environment.OpenInBrowser(invite))
                    console.MarkupLine("[dim]Couldn't open a browser. Copy the link above.[/]");
                continue;
            }

            servers = await api.GetServerNamesAsync(token, ct);
            if (servers is { Count: > 0 })
            {
                console.MarkupLine($"[green]The bot is in:[/] {Markup.Escape(string.Join(", ", servers))}");
                return;
            }
            if (servers is null)
            {
                console.MarkupLine("[dim]Couldn't ask Discord which servers the bot is in. Carrying on.[/]");
                return;
            }
            console.MarkupLine("[yellow]Discord says the bot isn't in a server yet.[/] On the page the link opens, pick the server and press Authorise.");
        }
    }
}
