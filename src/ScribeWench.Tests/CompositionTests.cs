using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using ScribeWench.Bot.Commands;
using ScribeWench.CLI;
using ScribeWench.CLI.Summarize;
using ScribeWench.CLI.Wizard;
using ScribeWench.CLI.Wizard.Environments;
using ScribeWench.Core.Interfaces;
using ScribeWench.Core.Models;
using ScribeWench.Tests.Support;

namespace ScribeWench.Tests;

/// <summary>
/// What each command registers can be built: a missing or circular registration fails here, not
/// when the bot starts.
/// </summary>
public class CompositionTests
{
    private static ServiceProvider Build(Action<IServiceCollection> register)
    {
        var services = new ServiceCollection().AddLogging();
        register(services);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }

    [Fact]
    public async Task Run_BuildsTheBot_WithItsCommandsInHelpOrder()
    {
        await using var services = Build(s => s.AddScribeWench(new AppConfig()).AddDiscordBot());

        Assert.Single(services.GetServices<IHostedService>());
        Assert.Equal("""
            **ScribeWench**
            `!scribe join <game>` — start recording
            `!scribe stop` — stop and write the summary
            `!scribe status` — what's being recorded
            `!scribe list-games` — show games
            `!scribe create-game <name>` — add a campaign or one-shot
            `!scribe remove-game <name>` — remove a game (its notes are kept)
            `!scribe edit <game> <what to change>` — correct the game's notes, in your own words
            `!scribe instructions-show <game>` — get the game's summary instructions as a file
            `!scribe instructions-edit <game>` — replace them with the attached .md file
            `!scribe instructions-reset <game>` — go back to the default instructions
            """.ReplaceLineEndings("\n"),
            services.GetRequiredService<CommandRouter>().HelpText());
    }

    [Fact]
    public async Task Summarize_BuildsItsRunner()
    {
        await using var services = Build(s => s.AddScribeWench(new AppConfig()).AddSummarize());

        Assert.NotNull(services.GetRequiredService<SummarizeRunner>());
    }

    [Fact]
    public async Task Setup_BuildsTheWizard_OnTheRealMachine()
    {
        await using var services = Build(s => s.AddSetupWizard());

        Assert.NotNull(services.GetRequiredService<SetupWizard>());
        Assert.IsType<RealEnvironment>(services.GetRequiredService<ISetupEnvironment>());
        Assert.IsType<LocalOllama>(services.GetRequiredService<IOllamaProgram>());
    }

    [Fact]
    public async Task SetupDryRun_PutsThePretendMachineWebAndSettingsInPlaceOfTheRealOnes()
    {
        await using var services = Build(s => s.AddSetupWizard().AddDryRun(new DryRunOptions(Fresh: true)));

        Assert.NotNull(services.GetRequiredService<SetupWizard>());
        var environment = Assert.IsType<DryRunEnvironment>(services.GetRequiredService<ISetupEnvironment>());
        Assert.Same(environment, services.GetRequiredService<IOllamaProgram>());
        Assert.IsType<DryRunWeb>(services.GetRequiredService<IHttpClientFactory>());
        Assert.IsType<NoSavedSettings>(services.GetRequiredService<IConfigStore>());
    }

    [Fact]
    public async Task ABackendThatDoesNotExist_IsNamed_WithTheOnesThatDo()
    {
        await using var services = Build(s => s.AddScribeWench(new AppConfig { Ai = { Backend = "nope" } }));

        var ex = Assert.Throws<InvalidOperationException>(() => services.GetRequiredService<BackendRegistry>().Current);

        Assert.Equal("Unknown AI backend: 'nope'. Valid values: claudecode, anthropic, openai, ollama, llamasharp.", ex.Message);
    }

    [Theory]
    [InlineData("claudecode", "Claude opus (Claude Code)")]
    [InlineData("anthropic", "claude-opus-5-5")]
    [InlineData("OpenAI", "gpt-4o-mini")]
    [InlineData("ollama", "ollama/gemma4:12b")]
    [InlineData("llamasharp", "local LLM (LLamaSharp)")]
    public async Task EachBackend_IsFoundByItsNameInTheSettings_AndSaysWhatANoteWasGeneratedWith(string backend, string label)
    {
        await using var services = Build(s => s.AddScribeWench(new AppConfig { Ai = { Backend = backend } }));

        Assert.Equal(label, services.GetRequiredService<BackendRegistry>().Current.Label);
    }

    [Fact]
    public async Task APartOfTheSettingsSetToNullByHand_CountsAsLeftOut()
    {
        var config = new AppConfig { Ai = { Backend = "claudecode", LlamaSharp = null!, Ollama = null! }, Recording = null! };

        await using var services = Build(s => s.AddScribeWench(config).AddDiscordBot());

        Assert.Equal("claudecode", services.GetRequiredService<BackendRegistry>().Current.Key);
    }

    [Fact]
    public async Task ABadOllamaAddress_ShowsWhenTheBotStarts_NotAfterTheFirstSession()
    {
        var program = new FakeOllamaProgram();
        var web = new FakeWeb();
        var backend = new OllamaBackend(new OllamaConfig { BaseUrl = "192.168.50.81" }, web, new OllamaClient(web, program), program,
            NullLogger<OllamaBackend>.Instance);

        await Assert.ThrowsAsync<UriFormatException>(() => backend.CheckAsync(default));
    }
}
