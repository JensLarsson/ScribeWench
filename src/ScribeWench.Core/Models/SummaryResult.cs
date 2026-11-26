namespace ScribeWench.Core.Models;

// What a recording turned out to be: a game session, the group talking about the campaign out of
// character, or neither.
public enum RecordingKind { Session, Discussion, Neither }

// Markdown is the note the model wrote: the session note, or for a discussion its note, which may be empty.
public sealed record SummaryResult(
    string Markdown,
    string[] KeyEvents,
    string[] NpcsIntroduced,
    string[] PlayerActions,
    string Cliffhanger,
    RecordingKind Kind = RecordingKind.Session
)
{
    // What the recording was instead of a session, in one sentence.
    public string Reason { get; init; } = "";

    public bool NotASession => Kind != RecordingKind.Session;
}
