using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using ScribeWench.AI.Backends;
using ScribeWench.Core.Models;
using ScribeWench.Tests.Support;

namespace ScribeWench.Tests;

public class OllamaBackendTests
{
    /// <summary>Answers /api/chat from a script of replies and keeps the request bodies.</summary>
    private sealed class FakeOllama(params Func<JsonElement, (HttpStatusCode Status, string Body)>[] replies) : HttpMessageHandler, IHttpClientFactory
    {
        public List<JsonElement> Requests { get; } = [];

        public HttpClient CreateClient(string name) => new(this, disposeHandler: false);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Equal("http://localhost:11434/api/chat", request.RequestUri!.ToString());
            var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct)).RootElement.Clone();
            Requests.Add(body);
            var reply = replies[Math.Min(Requests.Count - 1, replies.Length - 1)](body);
            return new HttpResponseMessage(reply.Status) { Content = new StringContent(reply.Body, Encoding.UTF8, "application/json") };
        }
    }

    private static (HttpStatusCode, string) Ok(string content, int promptTokens = 100, string doneReason = "stop", string? thinking = null) =>
        (HttpStatusCode.OK, JsonSerializer.Serialize(new
        {
            message = new { role = "assistant", content, thinking },
            done = true,
            done_reason = doneReason,
            prompt_eval_count = promptTokens,
            eval_count = 50,
        }));

    private static (HttpStatusCode, string) Error(string message) =>
        (HttpStatusCode.BadRequest, JsonSerializer.Serialize(new { error = message }));

    private const string TooLong =
        "the prompt is longer than the context length currently available to the model; shorten the prompt, adjust the context length in settings, or use a model with a longer context length";

    private static OllamaBackend Service(IHttpClientFactory web, OllamaConfig? config = null, Func<bool>? start = null)
    {
        var program = new FakeOllamaProgram(start);
        return new(config ?? new OllamaConfig { Model = "gemma4:12b", ContextSize = 8192 }, web, new OllamaClient(web, program), program,
            NullLogger<OllamaBackend>.Instance);
    }

    private static List<TranscriptSegment> Transcript(int lines) =>
        Enumerable.Range(0, lines)
            .Select(i => new TranscriptSegment(i % 2 == 0 ? "Erik" : "Anna", 0, TimeSpan.FromSeconds(i * 10), TimeSpan.FromSeconds(i * 10 + 8),
                $"line-{i:D5} " + string.Join(' ', Enumerable.Repeat("goblin", 20))))
            .ToList();

    [Fact]
    public async Task EveryRequest_StatesTheWindow_CapsTheReply_AndTurnsThinkingOff()
    {
        var ollama = new FakeOllama(_ => Ok("## What Happened\nThings."));

        var summary = await Service(ollama).SummarizeAsync(Transcript(10), new SummarizationOptions("Orc", new DateTime(2026, 11, 22)));

        var request = Assert.Single(ollama.Requests);
        Assert.Equal("gemma4:12b", request.GetProperty("model").GetString());
        Assert.False(request.GetProperty("stream").GetBoolean());
        Assert.False(request.GetProperty("think").GetBoolean());
        Assert.False(request.GetProperty("truncate").GetBoolean());
        Assert.Equal(8192, request.GetProperty("options").GetProperty("num_ctx").GetInt32());
        Assert.Equal(2048, request.GetProperty("options").GetProperty("num_predict").GetInt32());
        Assert.Equal(0.2, request.GetProperty("options").GetProperty("temperature").GetDouble());
        Assert.Equal("system", request.GetProperty("messages")[0].GetProperty("role").GetString());
        Assert.Contains("TRANSCRIPT:\nErik: line-00000", request.GetProperty("messages")[1].GetProperty("content").GetString());
        Assert.StartsWith("## What Happened", summary.Markdown);
    }

    [Fact]
    public async Task Thinking_WhenTurnedOn_GetsRoomOfItsOwn()
    {
        var ollama = new FakeOllama(_ => Ok("## What Happened\nThings."));
        var config = new OllamaConfig { Model = "qwen3.5:9b", ContextSize = 16384, Think = true, Temperature = null };

        await Service(ollama, config).SummarizeAsync(Transcript(10), new SummarizationOptions("Orc", new DateTime(2026, 11, 22)));

        var request = Assert.Single(ollama.Requests);
        Assert.False(request.GetProperty("options").TryGetProperty("temperature", out _));     // null: the model's own default
        Assert.True(request.GetProperty("think").GetBoolean());
        Assert.Equal(4096 + 2048, request.GetProperty("options").GetProperty("num_predict").GetInt32());   // the note, plus the thinking
    }

    [Fact]
    public async Task LongSession_RefusedAsTooLong_IsReadInSmallerParts()
    {
        // Ollama counts 2 characters per token here, more tokens than first assumed, and refuses what doesn't fit.
        (HttpStatusCode, string) Reply(JsonElement request)
        {
            var messages = request.GetProperty("messages");
            var system = messages[0].GetProperty("content").GetString()!;
            var tokens = (system.Length + messages[1].GetProperty("content").GetString()!.Length) / 2;
            if (tokens > 8192) return Error(TooLong);
            return Ok(system.StartsWith("You are taking notes") ? "- the party walked on" : "## What Happened\nThey walked.", tokens);
        }
        var ollama = new FakeOllama(Reply);

        var summary = await Service(ollama).SummarizeAsync(Transcript(300), new SummarizationOptions("Orc", new DateTime(2026, 11, 22)));

        Assert.StartsWith("## What Happened", summary.Markdown);
        Assert.Contains(ollama.Requests, r => r.GetProperty("messages")[1].GetProperty("content").GetString()!.Contains("TRANSCRIPT, PART 2:"));
        Assert.Contains("NOTES ON THE TRANSCRIPT", ollama.Requests[^1].GetProperty("messages")[1].GetProperty("content").GetString());
    }

    [Fact]
    public async Task ModelThatRejectsTheThinkSetting_IsAskedAgainWithoutIt()
    {
        var ollama = new FakeOllama(
            _ => Error("registry.ollama.ai/library/mistral-nemo:latest does not support thinking"),
            _ => Ok("fine"));

        var reply = await Service(ollama).CompleteAsync("system", "user");

        Assert.Equal("fine", reply);
        Assert.True(ollama.Requests[0].TryGetProperty("think", out _));
        Assert.False(ollama.Requests[1].TryGetProperty("think", out _));
    }

    [Fact]
    public async Task Edit_ThatDoesNotFit_FailsWithAnExplanation_InsteadOfWorkingFromHalfTheNotes()
    {
        var refused = new FakeOllama(_ => Error(TooLong));
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Service(refused).CompleteAsync("system", "all the notes"));
        Assert.Contains("context window", ex.Message);
        Assert.Contains("ai.ollama.contextSize", ex.Message);

        // Accepted, but with no room left for the reply: the edits could come back cut off.
        var noRoom = new FakeOllama(_ => Ok("partial", promptTokens: 7000));
        ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Service(noRoom).CompleteAsync("system", "all the notes"));
        Assert.Contains("7,000", ex.Message);
    }

    [Fact]
    public async Task OtherErrors_SayWhatOllamaSaid()
    {
        var ollama = new FakeOllama(_ => (HttpStatusCode.InternalServerError, "{\"error\":\"llama runner process has terminated\"}"));

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => Service(ollama).CompleteAsync("system", "user"));

        Assert.Contains("llama runner process has terminated", ex.Message);
    }

    private const string Base = "http://localhost:11434";

    private static object ChatReply(string content) =>
        new { message = new { role = "assistant", content }, done = true, done_reason = "stop", prompt_eval_count = 100, eval_count = 50 };

    [Fact]
    public async Task ModelThatIsNotDownloadedYet_IsDownloaded_AndTheRequestMadeAgain()
    {
        var downloaded = false;
        var web = new FakeWeb()
            .On("POST", $"{Base}/api/chat", _ => downloaded
                ? FakeWeb.Json(ChatReply("fine"))
                : FakeWeb.Json(new { error = "model 'gemma4:12b' not found" }, HttpStatusCode.NotFound))
            .On("POST", $"{Base}/api/pull", _ =>
            {
                downloaded = true;
                return FakeWeb.Lines(new { status = "pulling aaa", digest = "sha256:aaa", total = 1000, completed = 1000 }, new { status = "success" });
            });

        var reply = await Service(web).CompleteAsync("system", "user");

        Assert.Equal("fine", reply);
        Assert.Equal("gemma4:12b", web.To("/api/pull").Single().Json.GetProperty("model").GetString());
        Assert.Equal(2, web.To("/api/chat").Count());
    }

    [Fact]
    public async Task ModelThatIsNotDownloaded_WithDownloadingTurnedOff_SaysHowToGetIt()
    {
        var web = new FakeWeb().On("POST", $"{Base}/api/chat", new { error = "model 'gemma4:12b' not found" }, HttpStatusCode.NotFound);
        var config = new OllamaConfig { Model = "gemma4:12b", ContextSize = 8192, AutoDownload = false };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Service(web, config).CompleteAsync("system", "user"));

        Assert.Contains("ollama pull gemma4:12b", ex.Message);
        Assert.Empty(web.To("/api/pull"));
    }

    [Fact]
    public async Task ModelNameOllamaDoesNotKnow_FailsWithThatReason_NotWithAnEndlessDownload()
    {
        var web = new FakeWeb()
            .On("POST", $"{Base}/api/chat", new { error = "model 'gemma4:13b' not found" }, HttpStatusCode.NotFound)
            .On("POST", $"{Base}/api/pull", _ => FakeWeb.Lines(new { status = "pulling manifest" }, new { error = "pull model manifest: file does not exist" }));
        var config = new OllamaConfig { Model = "gemma4:13b", ContextSize = 8192 };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Service(web, config).CompleteAsync("system", "user"));

        Assert.Contains("no model called 'gemma4:13b'", ex.Message);
        Assert.Single(web.To("/api/pull"));
    }

    [Fact]
    public async Task OllamaThatIsNotRunning_IsStarted_AndTheRequestMadeAgain()
    {
        var running = false;
        var web = new FakeWeb()
            .On("GET", $"{Base}/api/version", _ => running ? FakeWeb.Json(new { version = "0.40.18" }) : throw new HttpRequestException("connection refused"))
            .On("POST", $"{Base}/api/chat", _ => running ? FakeWeb.Json(ChatReply("fine")) : throw new HttpRequestException("connection refused"));

        var reply = await Service(web, start: () => running = true).CompleteAsync("system", "user");

        Assert.Equal("fine", reply);
    }

    [Fact]
    public async Task OllamaOnAnotherComputerThatDoesNotAnswer_IsReportedAsThat_WithoutStartingAnythingHere()
    {
        var tried = false;
        var config = new OllamaConfig { BaseUrl = "http://192.168.1.10:11434", Model = "gemma4:12b", ContextSize = 8192 };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Service(new FakeWeb(), config, () => tried = true).CompleteAsync("system", "user"));

        Assert.Contains("192.168.1.10", ex.Message);
        Assert.Contains("ai.ollama.baseUrl", ex.Message);
        Assert.False(tried);
    }

    [Fact]
    public async Task ReplyUsedUpOnThinking_IsReportedAsThat()
    {
        var ollama = new FakeOllama(_ => Ok("", doneReason: "length", thinking: "Let me think about this..."));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Service(ollama).CompleteAsync("system", "user"));

        Assert.Contains("thinking", ex.Message);
    }
}
