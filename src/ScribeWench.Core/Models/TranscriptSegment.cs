namespace ScribeWench.Core.Models;

public sealed record TranscriptSegment(
    string SpeakerLabel,
    ulong UserId,
    TimeSpan Start,
    TimeSpan End,
    string Text
);
