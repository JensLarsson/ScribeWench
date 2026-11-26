namespace ScribeWench.Core.Models;

// The shape of config.json. Renaming a property here renames its key in everyone's saved settings.
public sealed class AppConfig
{
    public DiscordConfig Discord { get; set; } = new();
    public WhisperConfig Whisper { get; set; } = new();
    public AiConfig Ai { get; set; } = new();
    public OutputConfig Output { get; set; } = new();
    public RecordingConfig Recording { get; set; } = new();
    public ObsidianConfig Obsidian { get; set; } = new();
}

public sealed class DiscordConfig
{
    public string BotToken { get; set; } = string.Empty;
    public string CommandPrefix { get; set; } = "!scribe";
    public ulong DefaultTextChannelId { get; set; }
}

public sealed class WhisperConfig
{
    public string ModelSize { get; set; } = "base";
    public string ModelDirectory { get; set; } = "~/.scribewench/models/whisper";
    public string Language { get; set; } = "en";
    // -1 = the processor.
    public int GpuDevice { get; set; } = -1;
    // Characters per transcript segment. Not in setup; edit config.json.
    public int MaxSegmentLength { get; set; } = 30;
}

public sealed class AiConfig
{
    public string Backend { get; set; } = "llamasharp";
    // The default summary instructions. Read on every summary; created with the built-in ones if missing.
    public string PromptFile { get; set; } = "~/.scribewench/summary-prompt.md";
    public LlamaSharpConfig LlamaSharp { get; set; } = new();
    public OpenAiConfig OpenAi { get; set; } = new();
    public AnthropicConfig Anthropic { get; set; } = new();
    public OllamaConfig Ollama { get; set; } = new();
    public ClaudeCodeConfig ClaudeCode { get; set; } = new();
}

public sealed class ClaudeCodeConfig
{
    // "opus", "sonnet", "haiku", or a full model name.
    public string Model { get; set; } = "opus";
    // low, medium, high, xhigh or max.
    public string Effort { get; set; } = "high";
    // "claude" finds it on PATH.
    public string ExecutablePath { get; set; } = "claude";
    public int TimeoutMinutes { get; set; } = 30;
}

public sealed class LlamaSharpConfig
{
    public string ModelPath { get; set; } = "~/.scribewench/models/llm/Mistral-Nemo-Instruct-2407-Q4_K_M.gguf";
    public bool AutoDownload { get; set; } = true;
    public string DownloadUrl { get; set; } = "https://huggingface.co/bartowski/Mistral-Nemo-Instruct-2407-GGUF/resolve/main/Mistral-Nemo-Instruct-2407-Q4_K_M.gguf";
    public int ContextSize { get; set; } = 4096;
    public int GpuLayerCount { get; set; } = 0;
    // "mistral", "llama3" or "gemma".
    public string ChatTemplate { get; set; } = "mistral";
}

// OpenAI, or anything that speaks the same chat API.
public sealed class OpenAiConfig
{
    public const string DefaultBaseUrl = "https://api.openai.com/v1";

    public string ApiKey { get; set; } = string.Empty;
    public string Model { get; set; } = "gpt-4o-mini";
    // Everything before "/chat/completions". Empty = OpenAI itself.
    public string BaseUrl { get; set; } = string.Empty;
    // The model's context window in tokens when a long session has to be read in parts. 0 = it takes one whole.
    public int ContextSize { get; set; }
}

public sealed class AnthropicConfig
{
    public string ApiKey { get; set; } = string.Empty;
    public string Model { get; set; } = "claude-opus-5-5";
}

public sealed class OllamaConfig
{
    public string BaseUrl { get; set; } = "http://localhost:11434";
    public string Model { get; set; } = "gemma4:12b";
    // Ollama's num_ctx. The default holds a three-hour session in one request, which gave cleaner
    // notes in testing than reading in parts.
    public int ContextSize { get; set; } = 65536;
    public bool Think { get; set; }
    // Low on purpose: at 0.2 Gemma made up fewer names than at its own default. Null = the model's default.
    public double? Temperature { get; set; } = 0.2;
    // Download a missing model when a summary needs it, instead of failing.
    public bool AutoDownload { get; set; } = true;
}

public sealed class OutputConfig
{
    public string Directory { get; set; } = "~/Documents/ScribeWench";
    public bool PostToDiscord { get; set; } = true;
    public bool SaveMarkdown { get; set; } = true;
    // Only for `scribewench summarize` without a game.
    public string CampaignName { get; set; } = "My Campaign";
}

public sealed class RecordingConfig
{
    public string TempDirectory { get; set; } = "~/.scribewench/tmp";
}

public sealed class ObsidianConfig
{
    public const string DefaultVaultPath = "~/Documents/ScribeWench";

    // The notes folder; each game gets a folder in it. Empty = none: summaries go to the output directory.
    public string VaultPath { get; set; } = DefaultVaultPath;
    // Session notes, and the notes on campaign discussions beside them.
    public string SessionsFolder { get; set; } = "Sessions";
    public string TranscriptsFolder { get; set; } = "Transcripts";
    public string AttachmentsFolder { get; set; } = "Attachments";
    // Who plays whom, without ".md".
    public string CastNote { get; set; } = "Cast";
    public bool IncludePreviousSession { get; set; } = true;
    // Off = note names only, plus the previous session.
    public bool SendFullNotes { get; set; } = true;
    // About four characters per token. Past it, the oldest session notes are sent by name only.
    public int MaxNoteCharacters { get; set; } = 400_000;
    // Keep the game's other notes up to date with what each session established. Needs SendFullNotes.
    public bool UpdateNotes { get; set; } = true;
}
