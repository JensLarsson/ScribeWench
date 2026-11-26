using ScribeWench.AI.Backends;
using ScribeWench.AI.Summaries;
using ScribeWench.Core.Models;
using Spectre.Console;

namespace ScribeWench.CLI.Wizard.Writers;

public static class WebAddress
{
    public static bool IsOne(string text) =>
        Uri.TryCreate(text.Trim(), UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https";

    public static bool Same(string a, string b) =>
        string.Equals(a.Trim().TrimEnd('/'), b.Trim().TrimEnd('/'), StringComparison.OrdinalIgnoreCase);
}

// OpenAI, or anything that speaks its API: a service with a key, or a server of the user's own.
public sealed class ServiceSetup(OpenAiModelCatalog catalog, WizardUi ui, IAnsiConsole console) : IWriterSetup
{
    // Suggested: models to put at the top of the list, when the service has them. Names age; ones it no longer has are skipped.
    private sealed record Preset(string Name, string BaseUrl, string KeysAt, string About, string[] Suggested);

    private static readonly Preset[] Presets =
    [
        new("OpenAI", OpenAiConfig.DefaultBaseUrl, "platform.openai.com", "the GPT models", ["gpt-6.2-sol", "gpt-6.2-astra", "gpt-6-luna"]),
        new("OpenRouter", "https://openrouter.ai/api/v1", "openrouter.ai", "hundreds of models from every maker, on one key", []),
        new("DeepSeek", "https://api.deepseek.com", "platform.deepseek.com", "", ["deepseek-v4-pro", "deepseek-flash"]),
        new("Moonshot (Kimi)", "https://api.moonshot.ai/v1", "platform.kimi.ai", "", ["kimi-k3.1-pro", "kimi-k3"]),
        new("Google Gemini", "https://generativelanguage.googleapis.com/v1beta/openai", "aistudio.google.com", "", ["gemini-4-hydrogen"]),
    ];

    // A window this large takes a session and a game's notes in one request, so it needs no dividing up.
    private const int LargeContextWindow = 200_000;

    public string Backend => "openai";

    public int Order(WriterFindings found) => 21;

    public string Label(WriterFindings found) => "OpenAI, DeepSeek, Kimi, Gemini, OpenRouter or another service   [dim]with its API key[/]";

    public string Describe(AiConfig ai) => $"{ai.OpenAi.Model}, at {new Uri(OpenAiBackend.BaseUrlOf(ai.OpenAi.BaseUrl)).Host}";

    public async Task<bool> ConfigureAsync(AiConfig ai, CancellationToken ct)
    {
        var openAi = ai.OpenAi;
        var currentUrl = OpenAiBackend.BaseUrlOf(openAi.BaseUrl);

        // Which service, and where it is.
        var choices = Presets
            .Select(s => ((Preset?)s, s.About.Length > 0 ? $"{s.Name}   [dim]{s.About}[/]" : s.Name))
            .ToList();
        var other = new Preset("Another", "", "", "", []);
        var back = new Preset("Back", "", "", "", []);
        choices.Add((other, "Another service, or a server of your own   [dim]anything with an OpenAI-compatible API[/]"));
        choices.Add((back, "Back"));

        var preset = ui.Choose("Which service?", choices.ToArray())!;
        if (preset == back) return false;

        string baseUrl;
        if (preset == other)
        {
            console.MarkupLine("[dim]The address is everything before /chat/completions. LM Studio on this PC, for one, is http://localhost:1234/v1[/]");
            var prompt = new TextPrompt<string>("Address of the API:")
                .Validate(text => WebAddress.IsOne(text) ? ValidationResult.Success() : ValidationResult.Error("An address like https://api.example.com/v1"));
            if (Presets.All(s => !WebAddress.Same(s.BaseUrl, currentUrl))) prompt.DefaultValue(currentUrl);
            baseUrl = console.Prompt(prompt).Trim().TrimEnd('/');
        }
        else
        {
            baseUrl = preset.BaseUrl;
            console.MarkupLine($"[dim]Keys are made at {preset.KeysAt}.[/]");
        }

        // Its key, checked by asking the service for its models.
        var name = preset == other ? "API" : preset.Name;
        var sameService = WebAddress.Same(baseUrl, currentUrl);
        if (preset == other) console.MarkupLine("[dim]Leave the key empty if the server needs none.[/]");
        var key = ui.AskSecret($"{name} key", sameService ? openAi.ApiKey : null, allowEmpty: preset == other);

        IReadOnlyList<ServiceModel> models = [];
        while (true)
        {
            try
            {
                models = await console.Status().StartAsync($"Asking {new Uri(baseUrl).Host} which models it has...",
                    _ => catalog.ListAsync(baseUrl, key, ct));
                break;
            }
            catch (InvalidOperationException ex)
            {
                console.MarkupLine($"[yellow]{Markup.Escape(ex.Message)}[/]");
                var next = ui.Choose("Then:",
                    ("key", "Enter the key again"),
                    ("skip", "Carry on, and type the model's name myself"),
                    ("back", "Choose another way to write the notes"));
                if (next == "back") return false;
                if (next == "skip") break;
                key = ui.AskSecret($"{name} key", null, allowEmpty: preset == other);
            }
        }

        var model = ChooseModel(models, preset, sameService ? openAi.Model : null);

        // How much the model reads at once: from the service's list, or for a server of one's own, asked.
        var contextSize = 0;
        var listed = models.FirstOrDefault(m => m.Id == model);
        if (listed is { ContextLength: > 0 })
        {
            if (listed.ContextLength < LargeContextWindow) contextSize = listed.ContextLength;
        }
        else if (preset == other)
        {
            console.MarkupLine("[dim]How many tokens the model reads at once is on its page at the service. When that is under 200,000, long sessions are[/]");
            console.MarkupLine("[dim]read in parts so they fit. Too low a number only costs time; too high and long sessions fail.[/]");
            var tokens = console.Prompt(
                new TextPrompt<int>("Context window in tokens:")
                    .DefaultValue(sameService && openAi.ContextSize > 0 ? openAi.ContextSize : 128_000)
                    .Validate(v => v >= ChunkedSummarizer.MinContextTokens ? ValidationResult.Success() : ValidationResult.Error($"At least {ChunkedSummarizer.MinContextTokens:N0}")));
            if (tokens < LargeContextWindow) contextSize = tokens;
        }
        if (contextSize > 0)
            console.MarkupLine($"[dim]{Markup.Escape(model)} reads {contextSize:N0} tokens at once, so a long session will be read in parts.[/]");

        openAi.BaseUrl = WebAddress.Same(baseUrl, OpenAiConfig.DefaultBaseUrl) ? "" : baseUrl;
        openAi.ApiKey = key;
        openAi.Model = model;
        openAi.ContextSize = contextSize;
        ai.Backend = Backend;
        return true;
    }

    private string ChooseModel(IReadOnlyList<ServiceModel> models, Preset preset, string? current)
    {
        var chat = models.Select(m => m.Id).Where(LooksLikeChatModel).Distinct().ToList();
        if (chat.Count == 0)
        {
            var prompt = new TextPrompt<string>("Model name, as the service spells it:")
                .Validate(text => string.IsNullOrWhiteSpace(text) ? ValidationResult.Error("The name can't be empty") : ValidationResult.Success());
            if (!string.IsNullOrWhiteSpace(current)) prompt.DefaultValue(current);
            return console.Prompt(prompt).Trim();
        }

        // What is in use now first, then the service's current main models, then the rest by name.
        var first = new List<string>();
        if (current is not null && chat.Contains(current)) first.Add(current);
        first.AddRange(preset.Suggested.Where(id => chat.Contains(id) && !first.Contains(id)));

        var choices = first.Select(id => (id, $"{Markup.Escape(id)}{(id == current ? "   [dim]current[/]" : "")}"))
            .Concat(chat.Except(first).OrderBy(id => id, StringComparer.OrdinalIgnoreCase).Select(id => (id, Markup.Escape(id))))
            .Append(("", "Another model: type its name"))
            .ToArray();

        if (chat.Count > 12) console.MarkupLine($"[dim]{chat.Count} models. Type part of a name to find it.[/]");
        var model = ui.Choose("Which model?", search: true, choices);
        return model.Length > 0
            ? model
            : console.Prompt(new TextPrompt<string>("Model name, as the service spells it:")
                .Validate(text => string.IsNullOrWhiteSpace(text) ? ValidationResult.Error("The name can't be empty") : ValidationResult.Success())).Trim();
    }

    // Leaves out what a service lists beside its chat models: embeddings, speech, images and the like.
    internal static bool LooksLikeChatModel(string id)
    {
        string[] others =
        [
            "embed", "whisper", "tts", "transcribe", "speech", "audio", "realtime", "dall-e", "image", "imagen", "veo", "sora",
            "video", "moderation", "rerank", "guard", "babbage", "davinci", "aqa", "robotics",
        ];
        return !others.Any(word => id.Contains(word, StringComparison.OrdinalIgnoreCase));
    }
}
