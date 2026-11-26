namespace ScribeWench.Core.Models;

public sealed class RecordingSession
{
    public ulong GuildId { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? StoppedAt { get; set; }
    public bool IsActive => StoppedAt is null;
}
