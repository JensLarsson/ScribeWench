using LLama;
using LLama.Common;
using LLama.Sampling;
using Microsoft.Extensions.Logging;
using ScribeWench.AI.Summaries;
using ScribeWench.Core;
using ScribeWench.Core.Models;

namespace ScribeWench.AI.Backends;

// A GGUF file run in this process. The old way, kept for those who use it: a small context window,
// so no game notes, no `edit`, and a long session is summarized in 45-minute chunks.
public sealed class LlamaSharpBackend(LlamaSharpConfig config, IHttpClientFactory factory, ILogger<LlamaSharpBackend> logger)
    : Backend(logger), IAsyncDisposable
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private LLamaWeights? _weights;
    private ModelParams? _modelParams;
    private bool _modelLoaded;

    public override string Key => "llamasharp";
    public override string Label => "local LLM (LLamaSharp)";
    protected override string Summarizing => "the built-in runner";

    // Half the context is kept for the reply.
    private int MaxOutputTokens => Math.Max(256, config.ContextSize / 2);

    public override async Task<SummaryResult> SummarizeAsync(
        IReadOnlyList<TranscriptSegment> transcript, SummarizationOptions options, CancellationToken ct = default)
    {
        await EnsureModelLoadedAsync(ct);

        // The game's notes are left out: they don't fit in a window this small.
        var userMessage = Prompts.BuildUserMessage(Prompts.FormatTranscript(transcript), options.CampaignName, options.SessionDateTime);
        var systemPrompt = Prompts.SystemPromptFor(options);

        // About four characters to a token.
        var estimatedInputTokens = (systemPrompt.Length + userMessage.Length) / 4;
        var inputBudget = config.ContextSize - MaxOutputTokens;
        if (estimatedInputTokens <= inputBudget)
            return SummaryParser.Parse(await InferAsync(systemPrompt, userMessage, ct));

        Log.LogWarning("Transcript too long (~{Tokens} tokens, budget {Budget}), chunking...", estimatedInputTokens, inputBudget);
        return SummaryParser.Parse(await SummarizeChunkedAsync(transcript, options, ct));
    }

    public override Task<string> CompleteAsync(string systemPrompt, string userMessage, CancellationToken ct = default) =>
        throw new NotSupportedException(
            "Editing notes needs a backend that can read all of a game's notes at once (Claude Code, Anthropic, OpenAI or Ollama), not a local LLamaSharp model.");

    private async Task EnsureModelLoadedAsync(CancellationToken ct)
    {
        if (_modelLoaded) return;
        await _lock.WaitAsync(ct);
        try
        {
            if (_modelLoaded) return;

            // The model file.
            var modelPath = PathHelper.Expand(config.ModelPath);
            if (!File.Exists(modelPath))
            {
                if (!config.AutoDownload || string.IsNullOrEmpty(config.DownloadUrl))
                    throw new FileNotFoundException($"LLM model not found at {modelPath}. Set AutoDownload=true or provide the file.");
                await DownloadModelAsync(modelPath, ct);
            }

            // In a single-file app the native loader has to be told where LLamaSharp's DLLs are.
            var nativeDir = Path.Combine(AppContext.BaseDirectory, "runtimes", "win-x64", "native");
            if (Directory.Exists(nativeDir))
                LLama.Native.NativeLibraryConfig.All.WithSearchDirectories([nativeDir]);

            Log.LogInformation("Loading LLamaSharp model from {Path}", modelPath);
            _modelParams = new ModelParams(modelPath)
            {
                ContextSize = (uint)config.ContextSize,
                GpuLayerCount = config.GpuLayerCount,
            };
            _weights = LLamaWeights.LoadFromFile(_modelParams);
            _modelLoaded = true;
            Log.LogInformation("LLamaSharp model loaded.");
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<string> InferAsync(string systemPrompt, string userMessage, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var executor = new StatelessExecutor(_weights!, _modelParams!);
            var inferParams = new InferenceParams
            {
                MaxTokens = MaxOutputTokens,
                AntiPrompts = config.ChatTemplate switch
                {
                    "llama3" => ["<|eot_id|>", "<|end_of_text|>"],
                    "gemma"  => ["<end_of_turn>"],
                    _        => ["[/INST]", "</s>"],
                },
                SamplingPipeline = new DefaultSamplingPipeline { Temperature = 0.7f },
                OverflowStrategy = ContextOverflowStrategy.TruncateAndReprefill,
            };

            // A prompt longer than the window crashes the runner (NoKvSlot), so cut it.
            var maxPromptChars = (config.ContextSize - MaxOutputTokens) * 4;
            if (userMessage.Length > maxPromptChars)
            {
                Log.LogWarning("Truncating input from {Actual} to {Max} chars to fit context window", userMessage.Length, maxPromptChars);
                userMessage = userMessage[..maxPromptChars];
            }

            var reply = new System.Text.StringBuilder();
            await foreach (var token in executor.InferAsync(BuildPrompt(systemPrompt, userMessage), inferParams, ct))
                reply.Append(token);

            var result = reply.ToString().Trim();
            if (result.StartsWith("<<SYS>>")) result = result["<<SYS>>".Length..].TrimStart();
            if (result.EndsWith("[/INST]"))  result = result[..^"[/INST]".Length].TrimEnd();
            return result;
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<string> SummarizeChunkedAsync(
        IReadOnlyList<TranscriptSegment> transcript, SummarizationOptions options, CancellationToken ct)
    {
        // 45-minute chunks that overlap by five.
        var chunkSize = TimeSpan.FromMinutes(45);
        var overlap = TimeSpan.FromMinutes(5);
        var chunks = new List<List<TranscriptSegment>>();
        var chunkStart = TimeSpan.Zero;
        while (true)
        {
            var chunkEnd = chunkStart + chunkSize;
            var chunk = transcript.Where(s => s.Start >= chunkStart && s.Start < chunkEnd).ToList();
            if (chunk.Count == 0) break;
            chunks.Add(chunk);

            if (chunk.Max(s => s.End) >= transcript[^1].End) break;
            chunkStart = chunkEnd - overlap;
        }

        // A summary of each.
        var summaries = new List<string>();
        for (var i = 0; i < chunks.Count; i++)
        {
            Log.LogInformation("Summarizing chunk {Index}/{Total}", i + 1, chunks.Count);
            var userMessage = Prompts.BuildUserMessage(Prompts.FormatTranscript(chunks[i]), options.CampaignName, options.SessionDateTime)
                + $"\n\n(This is part {i + 1} of {chunks.Count} of the session.)";
            summaries.Add(await InferAsync(Prompts.SystemPromptFor(options), userMessage, ct));
        }
        if (summaries.Count == 1) return summaries[0];

        // And one summary of those.
        var combined = string.Join("\n\n---\n\n", summaries.Select((s, i) => $"Part {i + 1}:\n{s}"));
        var rollup = $"Combine these {chunks.Count} partial summaries of the same D&D session into one cohesive summary:\n\n{combined}";
        return await InferAsync(Prompts.SystemPromptFor(options), rollup, ct);
    }

    private string BuildPrompt(string systemPrompt, string userMessage) =>
        config.ChatTemplate switch
        {
            "llama3" =>
                $"<|begin_of_text|><|start_header_id|>system<|end_header_id|>\n\n{systemPrompt}<|eot_id|>" +
                $"<|start_header_id|>user<|end_header_id|>\n\n{userMessage}<|eot_id|>" +
                $"<|start_header_id|>assistant<|end_header_id|>\n\n",

            "gemma" =>
                $"<start_of_turn>user\n{systemPrompt}\n\n{userMessage}<end_of_turn>\n<start_of_turn>model\n",

            // Mistral. <<SYS>> tags are Llama 2 only and don't belong here.
            _ =>
                $"[INST] {systemPrompt}\n\n{userMessage} [/INST]",
        };

    private async Task DownloadModelAsync(string destPath, CancellationToken ct)
    {
        Log.LogInformation("Downloading LLM model from {Url}...", config.DownloadUrl);
        Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);

        var http = factory.CreateClient("llamasharp");
        http.Timeout = TimeSpan.FromHours(2);
        using var response = await http.GetAsync(config.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        await using var file = File.Create(destPath);
        await stream.CopyToAsync(file, ct);
        Log.LogInformation("LLM model downloaded to {Path}", destPath);
    }

    public ValueTask DisposeAsync()
    {
        _weights?.Dispose();
        _lock.Dispose();
        return ValueTask.CompletedTask;
    }
}
