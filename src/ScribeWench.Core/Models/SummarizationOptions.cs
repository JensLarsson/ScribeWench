namespace ScribeWench.Core.Models;

public sealed record SummarizationOptions(string CampaignName, DateTime SessionDateTime)
{
    // Empty = the built-in instructions.
    public string SystemPrompt { get; init; } = string.Empty;

    // Placed before the transcript: the cast, the game's notes, the rules for the reply.
    public string? Context { get; init; }

    // Context cut down to a number of characters, for a model with a small context window: note
    // text goes, oldest sessions first, while every note stays listed by name. May still come out
    // longer than asked. Null when there is nothing to cut.
    public Func<int, string>? ContextWithin { get; init; }

    // What a model needs to follow one part of a transcript read in parts: the cast and the names.
    public string? PartContext { get; init; }
}
