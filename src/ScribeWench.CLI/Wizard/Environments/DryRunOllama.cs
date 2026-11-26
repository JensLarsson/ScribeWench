using System.Net;
using System.Text;
using System.Text.Json;
using ScribeWench.AI.Backends;

namespace ScribeWench.CLI.Wizard.Environments;

// The web as a dry run reaches it: everything real, except that talking to Ollama goes through the pretend one.
public sealed class DryRunWeb(IHttpMessageHandlerFactory real, DryRunOllama ollama) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) =>
        new(name == OllamaClient.HttpName ? ollama : real.CreateHandler(name), disposeHandler: false);
}

// Ollama as a dry run sees it. Questions (is it there, which models does it have) go to the real
// Ollama when there is one and the PC isn't being taken for a bare one. Downloading and loading a
// model never do: those are acted out, and the model then counts as downloaded for the rest of the run.
public sealed class DryRunOllama : HttpMessageHandler
{
    private readonly DryRunState _state;
    private readonly DryRunOptions _options;
    private readonly Func<MachineInfo> _machine;
    private readonly HttpMessageInvoker _real;

    // real: the handler that reaches the real Ollama; the network by default.
    public DryRunOllama(DryRunState state, DryRunOptions options, Func<MachineInfo> machine, HttpMessageHandler? real = null)
    {
        _state = state;
        _options = options;
        _machine = machine;
        _real = new HttpMessageInvoker(real ?? new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(3) }, disposeHandler: real is null);
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var path = request.RequestUri!.AbsolutePath;
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
        var model = ModelIn(body);

        // What would change something is never passed on.
        switch (request.Method.Method, path)
        {
            case ("POST", "/api/pull"):
                return Pull(model);
            case ("POST", "/api/chat"):
                return await LoadAsync(model, ct);
            case ("POST", "/api/generate"):
                _state.Loaded = null;
                return Json(new { done = true });
            case ("GET", "/api/ps"):
                return Json(new { models = _state.Loaded is { } loaded ? new[] { Loaded(loaded) } : [] });
        }

        // The real Ollama's answer, with what was "downloaded" in this run counted as there too.
        var real = _options.Fresh ? null : await AskRealAsync(request, body, ct);
        if (real is not null)
        {
            if (path == "/api/tags" && real.IsSuccessStatusCode && _state.Models.Count > 0)
                return Json(new { models = RealModels(await real.Content.ReadAsStringAsync(ct)).Concat(_state.Models.Select(Listed)) });
            if (path == "/api/show" && real.StatusCode == HttpStatusCode.NotFound && _state.Has(model))
                return Json(Shown(model));
            return real;
        }

        // No real one: the pretend one, once it has been "started".
        if (!_state.OllamaRunning)
            throw new HttpRequestException("Nothing is listening there (dry run: Ollama isn't running).");

        return path switch
        {
            "/api/version" => Json(new { version = "(pretend)" }),
            "/api/tags" => Json(new { models = _state.Models.Select(Listed) }),
            "/api/show" when _state.Has(model) => Json(Shown(model)),
            _ => Json(new { error = $"model '{model}' not found" }, HttpStatusCode.NotFound),
        };
    }

    // Null when there is no real Ollama to ask.
    private async Task<HttpResponseMessage?> AskRealAsync(HttpRequestMessage request, string body, CancellationToken ct)
    {
        try
        {
            using var copy = new HttpRequestMessage(request.Method, request.RequestUri);
            if (request.Content is not null) copy.Content = new StringContent(body, Encoding.UTF8, "application/json");
            return await _real.SendAsync(copy, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return null;
        }
    }

    // A download: the progress lines Ollama sends, a beat apart.
    private HttpResponseMessage Pull(string model)
    {
        var total = (long)((LocalModels.Find(model)?.DownloadGb ?? 8) * 1e9);
        var lines = new List<string> { JsonSerializer.Serialize(new { status = "pulling manifest" }) };
        for (var beat = 1; beat <= 30; beat++)
            lines.Add(JsonSerializer.Serialize(new { status = "pulling pretend", digest = "sha256:pretend", total, completed = total * beat / 30 }));
        lines.Add(JsonSerializer.Serialize(new { status = "success" }));

        _state.Models.Add(OllamaClient.Normalize(model));
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new SlowLines(lines, _options.Pace)) };
    }

    // Loading a model. One that plainly can't fit the (pretend) PC fails the way Ollama has it fail.
    private async Task<HttpResponseMessage> LoadAsync(string model, CancellationToken ct)
    {
        await Task.Delay(_options.Pace * 12, ct);

        var machine = _machine();
        if (LocalModels.Find(model) is { } known && known.MemoryGb > machine.RamGb + machine.CardGb)
            return Json(new { error = $"model requires more system memory ({known.MemoryGb:0.0} GiB) than is available ({machine.RamGb + machine.CardGb:0.0} GiB)" },
                HttpStatusCode.InternalServerError);

        _state.Loaded = model;
        return Json(new { message = new { role = "assistant", content = "ready" }, done = true });
    }

    private object Loaded(string model)
    {
        var known = LocalModels.Find(model);
        var size = (long)((known?.MemoryGb ?? 8) * 1e9);
        var share = known is not null ? LocalModels.CardShare(known, _machine().CardGb) : _machine().CardGb > 1 ? 1 : 0;
        return new { name = model, size, size_vram = (long)(size * share) };
    }

    private static object Listed(string model) => new
    {
        name = model,
        size = (long)((LocalModels.Find(model)?.DownloadGb ?? 8) * 1e9),
        details = new { context_length = 262144 },
        capabilities = new[] { "completion", "thinking" },
    };

    private static object Shown(string model) => new
    {
        details = new { parameter_size = "", quantization_level = "", context_length = 262144 },
        capabilities = new[] { "completion", "thinking" },
    };

    private static IEnumerable<object> RealModels(string tags)
    {
        using var doc = JsonDocument.Parse(tags);
        if (!doc.RootElement.TryGetProperty("models", out var models) || models.ValueKind != JsonValueKind.Array) return [];
        return models.EnumerateArray().Select(m => (object)m.Clone()).ToList();
    }

    private static string ModelIn(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("model", out var model) ? model.GetString() ?? "" : "";
        }
        catch (JsonException)
        {
            return "";
        }
    }

    private static HttpResponseMessage Json(object json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(JsonSerializer.Serialize(json), Encoding.UTF8, "application/json") };

    // One line per read, a beat apart, the way a download's progress arrives.
    private sealed class SlowLines(IReadOnlyList<string> lines, TimeSpan pace) : Stream
    {
        private int _next;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (_next >= lines.Count) return 0;
            await Task.Delay(pace, ct);
            var bytes = Encoding.UTF8.GetBytes(lines[_next++] + "\n");
            bytes.CopyTo(buffer);
            return bytes.Length;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
            ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
