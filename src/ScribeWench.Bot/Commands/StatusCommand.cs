namespace ScribeWench.Bot.Commands;

public sealed class StatusCommand(SessionManager sessions) : IScribeCommand
{
    public string Name => "status";
    public string Usage => "status";
    public string Help => "what's being recorded";

    public Task ExecuteAsync(CommandRequest request) => request.ReplyAsync(sessions.GetStatus());
}
