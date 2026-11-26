using Discord;

namespace ScribeWench.Bot.Commands;

public sealed class StopCommand(SessionManager sessions) : IScribeCommand
{
    public string Name => "stop";
    public string Usage => "stop";
    public string Help => "stop and write the summary";

    public async Task ExecuteAsync(CommandRequest request)
    {
        if (sessions.IsRecording)
            await sessions.StopAndProcessAsync(request.Channel);
        else if ((sessions.IsFinishing || sessions.IsProcessing) && sessions.CurrentGame is { } finishing)
            await request.ReplyAsync($"Already finishing **{Format.Sanitize(finishing.Name)}**.");
        else
            await request.ReplyAsync("Nothing is being recorded.");
    }
}
