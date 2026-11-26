using Discord.WebSocket;
using Microsoft.Extensions.DependencyInjection;
using ScribeWench.AI.Backends;
using ScribeWench.AI.Notes;
using ScribeWench.AI.Summaries;
using ScribeWench.Bot;
using ScribeWench.Bot.Commands;
using ScribeWench.CLI.Config;
using ScribeWench.CLI.Summarize;
using ScribeWench.CLI.Wizard;
using ScribeWench.CLI.Wizard.Automatic;
using ScribeWench.CLI.Wizard.Environments;
using ScribeWench.CLI.Wizard.Steps;
using ScribeWench.CLI.Wizard.Writers;
using ScribeWench.Core;
using ScribeWench.Core.Interfaces;
using ScribeWench.Core.Models;
using ScribeWench.Transcription;
using Spectre.Console;

namespace ScribeWench.CLI;

// The composition root: every registration is here. Which of these a command calls depends on
// what was typed, so each command picks its own after reading its settings.
public static class Composition
{
    // What `run` and `summarize` share: the settings, the backends, the notes and the transcriber.
    public static IServiceCollection AddScribeWench(this IServiceCollection services, AppConfig config)
    {
        // ── Settings (each part of config.json on its own; one set to null by hand counts as left out) ──
        var ai = config.Ai ?? new();
        services.AddSingleton(config.Discord ?? new());
        services.AddSingleton(config.Whisper ?? new());
        services.AddSingleton(ai);
        services.AddSingleton(ai.ClaudeCode ?? new());
        services.AddSingleton(ai.Anthropic ?? new());
        services.AddSingleton(ai.OpenAi ?? new());
        services.AddSingleton(ai.Ollama ?? new());
        services.AddSingleton(ai.LlamaSharp ?? new());
        services.AddSingleton(config.Output ?? new());
        services.AddSingleton(config.Recording ?? new());
        services.AddSingleton(config.Obsidian ?? new());
        services.AddSingleton(ScribePaths.Default);
        services.AddSingleton(TimeProvider.System);
        services.AddHttpClient();
        services.AddSingleton<GameStore>();

        // ── Backends (one strategy each; see IBackend) ───────────────────────────
        services.AddSingleton<ClaudeCli>();
        services.AddSingleton<IOllamaProgram, LocalOllama>();
        services.AddSingleton<OllamaClient>();
        services.AddBackend<ClaudeCodeBackend>();
        services.AddBackend<AnthropicBackend>();
        services.AddBackend<OpenAiBackend>();
        services.AddBackend<OllamaBackend>();
        services.AddBackend<LlamaSharpBackend>();
        services.AddSingleton<BackendRegistry>();

        // ── Notes and summaries ──────────────────────────────────────────────────
        services.AddNoteStore();
        services.AddSingleton<InstructionsStore>();
        services.AddSingleton<SessionSummarizer>();

        // ── Transcription ────────────────────────────────────────────────────────
        services.AddSingleton<WhisperModelFiles>();
        services.AddSingleton<ITranscriptionService, WhisperTranscriptionService>();
        return services;
    }

    public static IServiceCollection AddDiscordBot(this IServiceCollection services)
    {
        services.AddSingleton(_ => new DiscordSocketClient(BotService.SocketConfig));
        services.AddSingleton<SessionManager>();
        services.AddSingleton<AttachmentDownloader>();
        services.AddSingleton<GameLookup>();
        services.AddSingleton<InstructionsConfirmations>();

        // ── Commands (one strategy each; help lists them in this order) ──────────
        services.AddScribeCommand<JoinCommand>();
        services.AddScribeCommand<StopCommand>();
        services.AddScribeCommand<StatusCommand>();
        services.AddScribeCommand<ListGamesCommand>();
        services.AddScribeCommand<CreateGameCommand>();
        services.AddScribeCommand<RemoveGameCommand>();
        services.AddScribeCommand<EditCommand>();
        services.AddScribeCommand<InstructionsShowCommand>();
        services.AddScribeCommand<InstructionsEditCommand>();
        services.AddScribeCommand<InstructionsResetCommand>();
        services.AddSingleton<CommandRouter>();

        services.AddHostedService<BotService>();
        return services;
    }

    public static IServiceCollection AddSummarize(this IServiceCollection services)
    {
        services.AddSingleton(AnsiConsole.Console);
        services.AddSingleton<GameResolver>();
        services.AddSingleton<ISessionSource, TranscriptFileSource>();
        // Whatever no other source reads is taken as audio.
        services.AddSingleton<AudioFileSource>();
        services.AddSingleton<SummarizeRunner>();
        return services;
    }

    // configPath: a settings file other than the usual one.
    public static IServiceCollection AddSetupWizard(this IServiceCollection services, string? configPath = null)
    {
        // ── What setup works with ────────────────────────────────────────────────
        services.AddLogging();
        services.AddHttpClient();
        services.AddSingleton(AnsiConsole.Console);
        services.AddSingleton(ScribePaths.Default);
        services.AddSingleton(sp => new JsonConfigStore(sp.GetRequiredService<ScribePaths>(), configPath));
        services.AddSingleton<IConfigStore>(sp => sp.GetRequiredService<JsonConfigStore>());
        services.AddSingleton<ClaudeCli>();
        services.AddSingleton<WhisperModelFiles>();
        services.AddSingleton<LocalOllama>();
        services.AddSingleton<IOllamaProgram>(sp => sp.GetRequiredService<LocalOllama>());
        services.AddSingleton<RealEnvironment>();
        services.AddSingleton<ISetupEnvironment>(sp => sp.GetRequiredService<RealEnvironment>());
        services.AddSingleton<OllamaClient>();
        services.AddSingleton<OpenAiModelCatalog>();
        services.AddSingleton<DiscordApi>();
        services.AddSingleton<WizardUi>();
        services.AddSingleton<ITerminal, ConsoleTerminal>();
        services.AddSingleton<Banner>();
        services.AddSingleton<SetupSession>();

        // ── Steps (one strategy each; the menu lists them in this order) ─────────
        services.AddWizardStep<DiscordStep>();
        services.AddWizardStep<TranscriptionStep>();
        services.AddWizardStep<SummariesStep>();
        services.AddWizardStep<NotesStep>();

        // ── Ways to get the notes written (see IWriterSetup) ─────────────────────
        services.AddWriterSetup<ClaudeCodeSetup>();
        services.AddWriterSetup<LocalModelSetup>();
        services.AddWriterSetup<AnthropicSetup>();
        services.AddWriterSetup<ServiceSetup>();
        services.AddWriterSetup<BuiltInSetup>();

        // ── "Do it for me" (see IAutomaticWriter; notices are given in this order) ─
        services.AddSingleton<IAutomaticWriter, AutomaticLocalModel>();
        services.AddSingleton<IAutomaticWriter, AutomaticClaudeCode>();
        services.AddSingleton<AutomaticService>();
        services.AddSingleton<IAutomaticWriter>(sp => sp.GetRequiredService<AutomaticService>());
        services.AddSingleton<AutomaticStep>();

        services.AddSingleton<SetupWizard>();
        return services;
    }

    // `setup --dry-run`: the same wizard, with a machine and an Ollama that only act things out.
    public static IServiceCollection AddDryRun(this IServiceCollection services, DryRunOptions options)
    {
        services.AddSingleton(options);
        services.AddSingleton<DryRunState>();
        services.AddSingleton(sp => new DryRunEnvironment(
            options,
            sp.GetRequiredService<DryRunState>(),
            sp.GetRequiredService<RealEnvironment>(),
            sp.GetRequiredService<LocalOllama>(),
            sp.GetRequiredService<IAnsiConsole>()));
        services.AddSingleton<ISetupEnvironment>(sp => sp.GetRequiredService<DryRunEnvironment>());
        services.AddSingleton<IOllamaProgram>(sp => sp.GetRequiredService<DryRunEnvironment>());

        // Everything on the web is real, except Ollama. The machine is asked for only when a model
        // is loaded, by which time the environment exists.
        services.AddSingleton<IHttpClientFactory>(sp => new DryRunWeb(
            sp.GetRequiredService<IHttpMessageHandlerFactory>(),
            new DryRunOllama(sp.GetRequiredService<DryRunState>(), options, () => sp.GetRequiredService<DryRunEnvironment>().DetectMachine())));

        if (options.Fresh)
            services.AddSingleton<IConfigStore>(sp => new NoSavedSettings(sp.GetRequiredService<JsonConfigStore>()));
        return services;
    }

    private static IServiceCollection AddWizardStep<TStep>(this IServiceCollection services) where TStep : class, IWizardStep
    {
        // As itself too: the wizard asks for some of them by name.
        services.AddSingleton<TStep>();
        return services.AddSingleton<IWizardStep>(sp => sp.GetRequiredService<TStep>());
    }

    private static IServiceCollection AddWriterSetup<TSetup>(this IServiceCollection services) where TSetup : class, IWriterSetup
    {
        services.AddSingleton<TSetup>();
        return services.AddSingleton<IWriterSetup>(sp => sp.GetRequiredService<TSetup>());
    }
}
