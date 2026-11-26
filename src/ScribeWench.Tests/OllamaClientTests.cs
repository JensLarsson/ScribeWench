using System.Net;
using ScribeWench.AI.Backends;
using ScribeWench.Tests.Support;

namespace ScribeWench.Tests;

public class OllamaClientTests
{
    private const string Base = "http://localhost:11434";

    private static OllamaClient Client(FakeWeb web, Func<bool>? start = null) => new(web, new FakeOllamaProgram(start));

    // As Ollama 0.40.18 answers /api/tags.
    private const string Tags = """
        {"models":[
          {"name":"qwen3.8:27b","model":"qwen3.8:27b","size":17741872154,
           "details":{"parameter_size":"27.3B","quantization_level":"Q4_K_M","context_length":262144},
           "capabilities":["completion","vision","tools","thinking"]},
          {"name":"llama3:latest","model":"llama3:latest","size":4661224676,
           "details":{"parameter_size":"8.0B","quantization_level":"Q4_0"}}
        ]}
        """;

    [Fact]
    public async Task Version_IsNull_WhenNothingAnswers()
    {
        Assert.Null(await Client(new FakeWeb()).GetVersionAsync(Base));

        var running = new FakeWeb().On("GET", $"{Base}/api/version", new { version = "0.40.18" });
        Assert.Equal("0.40.18", await Client(running).GetVersionAsync(Base));
    }

    [Fact]
    public async Task List_ReadsWhatIsDownloaded_WithEachModelsWindowAndAbilities()
    {
        var web = new FakeWeb().On("GET", $"{Base}/api/tags", Tags);

        var models = await Client(web).ListAsync(Base);

        Assert.Equal(2, models.Count);
        Assert.Equal("qwen3.8:27b", models[0].Name);
        Assert.Equal(17741872154, models[0].SizeBytes);
        Assert.Equal(262144, models[0].ContextLength);
        Assert.Equal("Q4_K_M", models[0].Quantization);
        Assert.True(models[0].CanThink);
        Assert.Equal(0, models[1].ContextLength);      // older Ollama: not stated
        Assert.False(models[1].CanThink);
    }

    [Fact]
    public async Task Has_TreatsANameWithoutATagAsLatest()
    {
        var web = new FakeWeb().On("GET", $"{Base}/api/tags", Tags);

        Assert.True(await Client(web).HasAsync(Base, "llama3"));
        Assert.True(await Client(web).HasAsync(Base, "QWEN3.8:27b"));
        Assert.False(await Client(web).HasAsync(Base, "qwen3.8"));          // :latest is not :27b
        Assert.Equal("hf.co/someone/model-GGUF:latest", OllamaClient.Normalize("hf.co/someone/model-GGUF"));
        Assert.Equal("localhost:5000/team/model:q4", OllamaClient.Normalize("localhost:5000/team/model:q4"));
    }

    [Fact]
    public async Task Show_FindsTheWindowUnderTheModelsArchitecture_AndIsNullForAModelNotThere()
    {
        var web = new FakeWeb()
            .On("POST", $"{Base}/api/show", request => request.Json.GetProperty("model").GetString() == "gemma4:12b"
                ? FakeWeb.Json("""{"details":{"parameter_size":"11.9B","quantization_level":"Q4_K_M"},"model_info":{"general.architecture":"gemma4","gemma4.context_length":262144,"gemma4.block_count":48},"capabilities":["completion","thinking"]}""")
                : FakeWeb.Json(new { error = "model 'nosuchmodel:1b' not found" }, HttpStatusCode.NotFound));

        var model = await Client(web).ShowAsync(Base, "gemma4:12b");

        Assert.Equal(262144, model!.ContextLength);
        Assert.True(model.CanThink);
        Assert.Null(await Client(web).ShowAsync(Base, "nosuchmodel:1b"));
    }

    [Fact]
    public async Task Pull_AddsUpTheFilesAModelIsMadeOf()
    {
        var web = new FakeWeb().On("POST", $"{Base}/api/pull", _ => FakeWeb.Lines(
            new { status = "pulling manifest" },
            new { status = "pulling aaa", digest = "sha256:aaa", total = 1000, completed = 250 },
            new { status = "pulling aaa", digest = "sha256:aaa", total = 1000, completed = 1000 },
            new { status = "pulling bbb", digest = "sha256:bbb", total = 200, completed = 100 },
            new { status = "pulling bbb", digest = "sha256:bbb", total = 200, completed = 200 },
            new { status = "verifying sha256 digest" },
            new { status = "success" }));
        var seen = new List<OllamaPullProgress>();

        await Client(web).PullAsync(Base, "gemma4:12b", seen.Add);

        Assert.Equal("gemma4:12b", web.Requests.Single().Json.GetProperty("model").GetString());
        Assert.Contains(seen, p => p is { CompletedBytes: 250, TotalBytes: 1000 });
        Assert.Contains(seen, p => p is { CompletedBytes: 1100, TotalBytes: 1200 });
        Assert.Equal(new OllamaPullProgress("success", 1200, 1200), seen[^1]);
    }

    [Fact]
    public async Task Pull_OfANameOllamaDoesNotHave_SaysSo()
    {
        // Ollama answers 200 and puts the failure in the stream.
        var web = new FakeWeb().On("POST", $"{Base}/api/pull", _ => FakeWeb.Lines(
            new { status = "pulling manifest" },
            new { error = "pull model manifest: file does not exist" }));

        var ex = await Assert.ThrowsAsync<OllamaException>(() => Client(web).PullAsync(Base, "qwen3.8:72b"));

        Assert.Contains("no model called 'qwen3.8:72b'", ex.Message);
        Assert.Contains("ollama.com/library", ex.Message);
    }

    [Fact]
    public async Task Pull_ThatStopsHalfway_IsNotTakenForDone()
    {
        var web = new FakeWeb().On("POST", $"{Base}/api/pull", _ => FakeWeb.Lines(
            new { status = "pulling aaa", digest = "sha256:aaa", total = 1000, completed = 250 }));

        var ex = await Assert.ThrowsAsync<OllamaException>(() => Client(web).PullAsync(Base, "gemma4:12b"));

        Assert.Contains("stopped before it finished", ex.Message);
    }

    [Fact]
    public async Task TryLoad_ReportsHowMuchSitsOnTheCard_AndFreesTheMemoryAgain()
    {
        var web = new FakeWeb()
            .On("POST", $"{Base}/api/chat", new { message = new { role = "assistant", content = "ready" }, done = true })
            .On("GET", $"{Base}/api/ps", new { models = new[] { new { name = "qwen3.8:27b", size = 22_000_000_000, size_vram = 13_000_000_000 } } })
            .On("POST", $"{Base}/api/generate", new { done = true });

        var result = await Client(web).TryLoadAsync(Base, "qwen3.8:27b", 65536, TimeSpan.Zero);

        Assert.True(result.Ok);
        Assert.Equal(59, result.GraphicsPercent);
        var chat = web.To("/api/chat").Single().Json;
        Assert.Equal(65536, chat.GetProperty("options").GetProperty("num_ctx").GetInt32());
        Assert.False(chat.GetProperty("think").GetBoolean());
        Assert.Equal(0, web.To("/api/generate").Single().Json.GetProperty("keep_alive").GetInt32());
    }

    [Fact]
    public async Task TryLoad_OfAModelTooLargeForTheMachine_GivesOllamasReason()
    {
        var web = new FakeWeb().On("POST", $"{Base}/api/chat",
            new { error = "model requires more system memory (41.2 GiB) than is available (27.9 GiB)" }, HttpStatusCode.InternalServerError);

        var result = await Client(web).TryLoadAsync(Base, "qwen3.8:27b-q8_0", 65536, TimeSpan.Zero);

        Assert.False(result.Ok);
        Assert.Contains("more system memory", result.Error);
    }

    [Fact]
    public async Task EnsureRunning_StartsTheInstalledOllama_WhenNothingAnswersOnThisMachine()
    {
        var started = false;
        var web = new FakeWeb().On("GET", $"{Base}/api/version", _ => started
            ? FakeWeb.Json(new { version = "0.40.18" })
            : throw new HttpRequestException("connection refused"));

        var version = await Client(web, () => started = true).EnsureRunningAsync(Base, TimeSpan.FromSeconds(10));

        Assert.Equal("0.40.18", version);
        Assert.True(started);
    }

    [Fact]
    public async Task EnsureRunning_DoesNotTryToStartAnOllamaOnAnotherComputer()
    {
        var tried = false;

        var version = await Client(new FakeWeb(), () => tried = true).EnsureRunningAsync("http://192.168.1.10:11434", TimeSpan.FromSeconds(1));

        Assert.Null(version);
        Assert.False(tried);
        Assert.True(OllamaClient.IsLocal("http://127.0.0.1:11434"));
        Assert.True(OllamaClient.IsLocal(""));
        Assert.False(OllamaClient.IsLocal("http://192.168.1.10:11434"));
    }

    [Fact]
    public async Task EnsureRunning_GivesUp_WhenOllamaIsNotInstalled()
    {
        var version = await Client(new FakeWeb(), () => false).EnsureRunningAsync(Base, TimeSpan.FromSeconds(1));

        Assert.Null(version);
    }
}
