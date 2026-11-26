namespace ScribeWench.Core.Models;

// A campaign or one-shot that sessions are recorded for, per Discord server.
public sealed class Game
{
    public string Name { get; set; } = string.Empty;
    public ulong GuildId { get; set; }
    // Relative to the notes folder and to the output directory.
    public string Folder { get; set; } = string.Empty;
    public ulong CreatedByUserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    // Recorded sessions that had speech in them.
    public int SessionCount { get; set; }
    public DateTimeOffset? LastSessionAt { get; set; }
}
