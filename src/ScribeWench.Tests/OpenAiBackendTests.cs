using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using ScribeWench.AI.Backends;
using ScribeWench.Core.Models;
using ScribeWench.Tests.Support;

namespace ScribeWench.Tests;

public class OpenAiBackendTests
{
    private static OpenAiBackend Service(FakeWeb web, OpenAiConfig config) =>
        new(config, web, NullLogger<OpenAiBackend>.Instance, retryDelay: TimeSpan.Zero);

    private static object Reply(string content, int promptTokens = 100, string finishReason = "stop") => new
    {
        choices = new[] { new { message = new { role = "assistant", content }, finish_reason = finishReason } },
        usage = new { prompt_tokens = promptTokens, completion_tokens = 50 },
    };

    private static List<TranscriptSegment> Transcript(int lines) =>
        Enumerable.Range(0, lines)
            .Select(i => new TranscriptSegment(i % 2 == 0 ? "Erik" : "Anna", 0, TimeSpan.FromSeconds(i * 10), TimeSpan.FromSeconds(i * 10 + 8),
                $"line-{i:D5} " + string.Join(' ', Enumerable.Repeat("goblin", 20))))
            .ToList();

    [Fact]
    public async Task WithNoAddressSet_ItIsOpenAi()
    {
        var web = new FakeWeb().On("POST", "https://api.openai.com/v1/chat/completions", Reply("## What Happened\nThings."));
        var config = new OpenAiConfig { ApiKey = "sk-test", Model = "gpt-6.2-sol" };

        var summary = await Service(web, config).SummarizeAsync(Transcript(10), new SummarizationOptions("Orc", new DateTime(2026, 11, 22)));

        var request = web.Requests.Single();
        Assert.Equal("Bearer sk-test", request.Authorization);
        Assert.Equal("gpt-6.2-sol", request.Json.GetProperty("model").GetString());
        Assert.Equal("system", request.Json.GetProperty("messages")[0].GetProperty("role").GetString());
        Assert.Contains("TRANSCRIPT:", request.Json.GetProperty("messages")[1].GetProperty("content").GetString());
        // Only what every compatible service understands: no reply limit under either of its names.
        Assert.False(request.Json.TryGetProperty("max_tokens", out _));
        Assert.False(request.Json.TryGetProperty("max_completion_tokens", out _));
        Assert.StartsWith("## What Happened", summary.Markdown);
    }

    [Theory]
    [InlineData("https://api.deepseek.com", "https://api.deepseek.com/chat/completions")]
    [InlineData("https://openrouter.ai/api/v1/", "https://openrouter.ai/api/v1/chat/completions")]
    [InlineData("http://localhost:1234/v1", "http://localhost:1234/v1/chat/completions")]
    public async Task AnyServiceWithTheSameApi_IsReachedAtItsOwnAddress(string baseUrl, string expectedUrl)
    {
        var web = new FakeWeb().On("POST", expectedUrl, Reply("fine"));
        var config = new OpenAiConfig { ApiKey = "key", Model = "some-model", BaseUrl = baseUrl };

        Assert.Equal("fine", await Service(web, config).CompleteAsync("system", "user"));
        Assert.Equal(expectedUrl, web.Requests.Single().Url);
    }

    [Fact]
    public async Task ServerThatNeedsNoKey_GetsNoKeyHeader()
    {
        var web = new FakeWeb().On("POST", "http://localhost:1234/v1/chat/completions", Reply("fine"));
        var config = new OpenAiConfig { ApiKey = "", Model = "local-model", BaseUrl = "http://localhost:1234/v1" };

        await Service(web, config).CompleteAsync("system", "user");

        Assert.Null(web.Requests.Single().Authorization);
    }

    [Fact]
    public async Task BusyService_IsAskedAgain()
    {
        var calls = 0;
        var web = new FakeWeb().On("POST", "https://api.openai.com/v1/chat/completions", _ => ++calls switch
        {
            1 => FakeWeb.Json(new { error = new { message = "Rate limit reached", code = "rate_limit_exceeded" } }, HttpStatusCode.TooManyRequests),
            2 => FakeWeb.Json(new { error = new { message = "overloaded" } }, HttpStatusCode.ServiceUnavailable),
            _ => FakeWeb.Json(Reply("fine")),
        });

        Assert.Equal("fine", await Service(web, new OpenAiConfig { ApiKey = "k", Model = "m" }).CompleteAsync("system", "user"));
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task RejectedKey_IsReportedAsThat_AndNotRetried()
    {
        var web = new FakeWeb().On("POST", "https://api.deepseek.com/chat/completions",
            new { error = new { message = "Authentication Fails", type = "authentication_error" } }, HttpStatusCode.Unauthorized);
        var config = new OpenAiConfig { ApiKey = "wrong", Model = "deepseek-flash", BaseUrl = "https://api.deepseek.com" };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Service(web, config).CompleteAsync("system", "user"));

        Assert.Contains("api.deepseek.com rejected the API key", ex.Message);
        Assert.Single(web.Requests);
    }

    [Theory]
    [InlineData("""{"error":{"message":"This model's maximum context length is 128000 tokens. However, your messages resulted in 201000 tokens.","type":"invalid_request_error","code":"context_length_exceeded"}}""")]
    [InlineData("""{"error":{"message":"Input token length too long","type":"invalid_request_error"}}""")]
    [InlineData("""{"error":{"code":400,"message":"Provider returned error","metadata":{"error_type":"context_length_exceeded"}}}""")]
    public async Task RequestTooLongForTheModel_IsExplained_WithWhatToDoAboutIt(string body)
    {
        var web = new FakeWeb().On("POST", "https://api.openai.com/v1/chat/completions", body, HttpStatusCode.BadRequest);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Service(web, new OpenAiConfig { ApiKey = "k", Model = "small-model" }).CompleteAsync("system", "every note of the game"));

        Assert.Contains("too much for 'small-model'", ex.Message);
        Assert.Contains("ai.openAi.contextSize", ex.Message);
    }

    [Fact]
    public async Task WithAContextWindowSet_ALongSessionIsReadInParts()
    {
        // The service counts 3 characters per token and refuses what doesn't fit in 8,192.
        HttpResponseMessage Answer(FakeWeb.Request request)
        {
            var messages = request.Json.GetProperty("messages");
            var system = messages[0].GetProperty("content").GetString()!;
            var tokens = (system.Length + messages[1].GetProperty("content").GetString()!.Length) / 3;
            if (tokens > 8192)
                return FakeWeb.Json(new { error = new { message = "maximum context length is 8192 tokens", code = "context_length_exceeded" } }, HttpStatusCode.BadRequest);
            return FakeWeb.Json(Reply(system.StartsWith("You are taking notes") ? "- the party walked on" : "## What Happened\nThey walked.", tokens));
        }
        var web = new FakeWeb().On("POST", "http://localhost:1234/v1/chat/completions", Answer);
        var config = new OpenAiConfig { Model = "local-model", BaseUrl = "http://localhost:1234/v1", ContextSize = 8192 };

        var summary = await Service(web, config).SummarizeAsync(Transcript(300), new SummarizationOptions("Orc", new DateTime(2026, 11, 22)));

        Assert.StartsWith("## What Happened", summary.Markdown);
        Assert.Contains(web.Requests, r => r.Json.GetProperty("messages")[1].GetProperty("content").GetString()!.Contains("TRANSCRIPT, PART 2:"));
        Assert.Contains("NOTES ON THE TRANSCRIPT", web.Requests[^1].Json.GetProperty("messages")[1].GetProperty("content").GetString());
    }

    [Fact]
    public async Task ListModels_ReadsNamesAndWindows_AndShowsThatTheKeyWorks()
    {
        var web = new FakeWeb()
            .On("GET", "https://openrouter.ai/api/v1/models", new
            {
                data = new object[]
                {
                    new { id = "deepseek/deepseek-v4-pro", context_length = 1048576 },
                    new { id = "meta/llama-small", context_length = 32768 },
                },
            })
            .On("GET", "https://api.openai.com/v1/models", new { data = new[] { new { id = "gpt-6.2-sol", owned_by = "openai" } } });

        var openRouter = await new OpenAiModelCatalog(web).ListAsync("https://openrouter.ai/api/v1", "or-key");
        var openAi = await new OpenAiModelCatalog(web).ListAsync("", "sk-key");

        Assert.Equal([new ServiceModel("deepseek/deepseek-v4-pro", 1048576), new ServiceModel("meta/llama-small", 32768)], openRouter);
        Assert.Equal([new ServiceModel("gpt-6.2-sol", 0)], openAi);
        Assert.Equal("Bearer sk-key", web.Requests[^1].Authorization);
    }

    [Fact]
    public async Task ListModels_SaysWhetherTheKeyOrTheAddressIsWrong()
    {
        var web = new FakeWeb().On("GET", "https://api.openai.com/v1/models",
            new { error = new { message = "Incorrect API key provided", code = "invalid_api_key" } }, HttpStatusCode.Unauthorized);

        var rejected = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new OpenAiModelCatalog(web).ListAsync(null, "wrong"));
        var nobodyHome = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new OpenAiModelCatalog(web).ListAsync("http://localhost:1234/v1", ""));

        Assert.Contains("key was rejected", rejected.Message);
        Assert.Contains("Nothing answers at http://localhost:1234/v1", nobodyHome.Message);
    }
}
