using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace ScribeWench.AI.Backends;

// ContextLength: the most tokens the model reads at once; 0 when Ollama doesn't say.
public sealed record OllamaModel(
    string Name,
    long SizeBytes,
    int ContextLength,
    string ParameterSize,
    string Quantization,
    IReadOnlyList<string> Capabilities)
{
    public bool CanThink => Capabilities.Contains("thinking", StringComparer.OrdinalIgnoreCase);
}

// Summed over the files a model is made of.
public readonly record struct OllamaPullProgress(string Status, long CompletedBytes, long TotalBytes);

// SizeBytes: memory the loaded model takes, context window included; 0 when unknown.
public sealed record OllamaLoadResult(bool Ok, string? Error, TimeSpan Elapsed, long SizeBytes, long GraphicsBytes)
{
    // Null when Ollama didn't report sizes.
    public int? GraphicsPercent => SizeBytes > 0 ? (int)Math.Round(100.0 * Math.Min(GraphicsBytes, SizeBytes) / SizeBytes) : null;
}

// Something Ollama refused or failed at, in its own words.
public sealed class OllamaException(string message) : Exception(message);

// The parts of Ollama's API that aren't chatting: is it there, which models does it have, download
// one, try loading one. The address is given per call, since setup tries more than one.
public sealed class OllamaClient(IHttpClientFactory factory, IOllamaProgram program)
{
    public const string HttpName = "ollama";
    public const string DefaultBaseUrl = "http://localhost:11434";
    public const string DownloadPage = "https://ollama.com/download";

    private HttpClient Http()
    {
        var http = factory.CreateClient(HttpName);
        // Loading a large model for the first time can take minutes.
        http.Timeout = TimeSpan.FromMinutes(30);
        return http;
    }

    private static string Url(string baseUrl, string path) =>
        $"{(string.IsNullOrWhiteSpace(baseUrl) ? DefaultBaseUrl : baseUrl.Trim()).TrimEnd('/')}/{path}";

    // Whether the address is this machine, where a missing server can be started.
    public static bool IsLocal(string baseUrl) =>
        Uri.TryCreate(string.IsNullOrWhiteSpace(baseUrl) ? DefaultBaseUrl : baseUrl.Trim(), UriKind.Absolute, out var uri) && uri.IsLoopback;

    // "gemma4" and "gemma4:latest" are the same model to Ollama.
    public static string Normalize(string model)
    {
        model = model.Trim();
        var lastSegment = model[(model.LastIndexOf('/') + 1)..];
        return lastSegment.Contains(':') ? model : model + ":latest";
    }

    public static bool SameModel(string a, string b) =>
        string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);

    // Null when nothing answers at the address.
    public async Task<string?> GetVersionAsync(string baseUrl, CancellationToken ct = default)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            using var response = await Http().GetAsync(Url(baseUrl, "api/version"), timeout.Token);
            if (!response.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
            return doc.RootElement.TryGetProperty("version", out var version) ? version.GetString() ?? "" : "";
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            return null;
        }
    }

    // The models already downloaded.
    public async Task<IReadOnlyList<OllamaModel>> ListAsync(string baseUrl, CancellationToken ct = default)
    {
        using var response = await Http().GetAsync(Url(baseUrl, "api/tags"), ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode) throw new OllamaException(ErrorIn(body));

        using var doc = JsonDocument.Parse(body);
        var models = new List<OllamaModel>();
        if (doc.RootElement.TryGetProperty("models", out var list) && list.ValueKind == JsonValueKind.Array)
            foreach (var item in list.EnumerateArray())
                models.Add(ReadModel(Text(item, "name") ?? Text(item, "model") ?? "", item));
        return models;
    }

    public async Task<bool> HasAsync(string baseUrl, string model, CancellationToken ct = default) =>
        (await ListAsync(baseUrl, ct)).Any(m => SameModel(m.Name, model));

    // Null when Ollama doesn't have the model.
    public async Task<OllamaModel?> ShowAsync(string baseUrl, string model, CancellationToken ct = default)
    {
        using var response = await Http().PostAsJsonAsync(Url(baseUrl, "api/show"), new { model }, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        if (!response.IsSuccessStatusCode) throw new OllamaException(ErrorIn(body));

        using var doc = JsonDocument.Parse(body);
        return ReadModel(model, doc.RootElement);
    }

    // Downloads a model, picking up where an interrupted download stopped. Throws OllamaException
    // when Ollama has no model by that name or the download fails.
    public async Task PullAsync(string baseUrl, string model, Action<OllamaPullProgress>? onProgress = null, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Url(baseUrl, "api/pull"))
        {
            Content = JsonContent.Create(new { model, stream = true }),
        };
        // Headers only: the body is a stream of progress lines that runs as long as the download does.
        using var response = await Http().SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
            throw new OllamaException(ErrorIn(await response.Content.ReadAsStringAsync(ct)));

        var files = new Dictionary<string, (long Total, long Completed)>();
        var succeeded = false;

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (line.Length == 0) continue;
            JsonDocument doc;
            try { doc = JsonDocument.Parse(line); }
            catch (JsonException) { continue; }

            using (doc)
            {
                var root = doc.RootElement;
                if (Text(root, "error") is { Length: > 0 } error)
                    throw new OllamaException(DescribePullError(model, error));

                var status = Text(root, "status") ?? "";
                if (status == "success") succeeded = true;

                if (Text(root, "digest") is { } digest && root.TryGetProperty("total", out var total) && total.TryGetInt64(out var totalBytes))
                {
                    var completed = root.TryGetProperty("completed", out var done) && done.TryGetInt64(out var doneBytes) ? doneBytes : 0;
                    files[digest] = (totalBytes, completed);
                }
                onProgress?.Invoke(new OllamaPullProgress(status, files.Values.Sum(f => f.Completed), files.Values.Sum(f => f.Total)));
            }
        }

        if (!succeeded)
            throw new OllamaException($"The download of '{model}' stopped before it finished. Run it again to continue where it stopped.");
    }

    private static string DescribePullError(string model, string error) =>
        error.Contains("file does not exist", StringComparison.OrdinalIgnoreCase) || error.Contains("not found", StringComparison.OrdinalIgnoreCase)
            ? $"Ollama has no model called '{model}'. Check the name on https://ollama.com/library."
            : error.Contains("newer version", StringComparison.OrdinalIgnoreCase)
                ? $"'{model}' needs a newer Ollama than the one installed. Update Ollama ({DownloadPage}) and try again."
                : error;

    // Loads the model with the given context window and has it answer one word, to find out that
    // it runs before a session depends on it. Zero for keepLoaded frees its memory again at once.
    public async Task<OllamaLoadResult> TryLoadAsync(string baseUrl, string model, int contextSize, TimeSpan keepLoaded, CancellationToken ct = default)
    {
        // Load it.
        var timer = Stopwatch.StartNew();
        try
        {
            var (status, body) = await PostProbeAsync(baseUrl, model, contextSize, sendThink: true, ct);
            // Models that can't think refuse the setting on some Ollama versions.
            if (status >= 400 && body.Contains("think", StringComparison.OrdinalIgnoreCase))
                (status, body) = await PostProbeAsync(baseUrl, model, contextSize, sendThink: false, ct);
            if (status >= 400)
                return new OllamaLoadResult(false, ErrorIn(body), timer.Elapsed, 0, 0);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return new OllamaLoadResult(false, ex.Message, timer.Elapsed, 0, 0);
        }
        var elapsed = timer.Elapsed;

        // See how much of it landed on the graphics card.
        long size = 0, graphics = 0;
        try
        {
            using var response = await Http().GetAsync(Url(baseUrl, "api/ps"), ct);
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            if (doc.RootElement.TryGetProperty("models", out var list) && list.ValueKind == JsonValueKind.Array)
                foreach (var item in list.EnumerateArray())
                    if (SameModel(Text(item, "name") ?? Text(item, "model") ?? "", model))
                    {
                        size = Number(item, "size");
                        graphics = Number(item, "size_vram");
                    }
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException) { }

        if (keepLoaded <= TimeSpan.Zero)
            await UnloadAsync(baseUrl, model, ct);
        return new OllamaLoadResult(true, null, elapsed, size, graphics);
    }

    private async Task<(int Status, string Body)> PostProbeAsync(string baseUrl, string model, int contextSize, bool sendThink, CancellationToken ct)
    {
        var request = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["stream"] = false,
            ["messages"] = new[] { new { role = "user", content = "Reply with the single word: ready" } },
            ["options"] = new Dictionary<string, object> { ["num_ctx"] = contextSize, ["num_predict"] = 8 },
        };
        if (sendThink) request["think"] = false;

        using var response = await Http().PostAsJsonAsync(Url(baseUrl, "api/chat"), request, ct);
        return ((int)response.StatusCode, await response.Content.ReadAsStringAsync(ct));
    }

    // Failing is harmless: Ollama frees a model's memory after a few minutes anyway.
    public async Task UnloadAsync(string baseUrl, string model, CancellationToken ct = default)
    {
        try
        {
            using var response = await Http().PostAsJsonAsync(Url(baseUrl, "api/generate"), new { model, keep_alive = 0 }, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested) { }
    }

    // When nothing answers and the address is this machine, starts the installed Ollama and waits
    // for it. Returns its version, or null.
    public async Task<string?> EnsureRunningAsync(string baseUrl, TimeSpan? wait = null, CancellationToken ct = default)
    {
        if (await GetVersionAsync(baseUrl, ct) is { } version) return version;
        if (!IsLocal(baseUrl)) return null;
        if (!program.Start()) return null;

        var waited = Stopwatch.StartNew();
        var limit = wait ?? TimeSpan.FromSeconds(30);
        while (waited.Elapsed < limit)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(500), ct);
            if (await GetVersionAsync(baseUrl, ct) is { } started) return started;
        }
        return null;
    }

    private static OllamaModel ReadModel(string name, JsonElement element)
    {
        var details = element.TryGetProperty("details", out var d) && d.ValueKind == JsonValueKind.Object ? d : default;
        var context = details.ValueKind == JsonValueKind.Object ? (int)Math.Min(int.MaxValue, Number(details, "context_length")) : 0;

        // "show" keeps it under the model's architecture instead: "gemma4.context_length".
        if (context == 0 && element.TryGetProperty("model_info", out var info) && info.ValueKind == JsonValueKind.Object)
            foreach (var property in info.EnumerateObject())
                if (property.Name.EndsWith(".context_length", StringComparison.Ordinal) && property.Value.TryGetInt64(out var length))
                    context = (int)Math.Min(int.MaxValue, length);

        var capabilities = new List<string>();
        if (element.TryGetProperty("capabilities", out var caps) && caps.ValueKind == JsonValueKind.Array)
            foreach (var capability in caps.EnumerateArray())
                if (capability.GetString() is { } text) capabilities.Add(text);

        return new OllamaModel(
            name,
            Number(element, "size"),
            context,
            details.ValueKind == JsonValueKind.Object ? Text(details, "parameter_size") ?? "" : "",
            details.ValueKind == JsonValueKind.Object ? Text(details, "quantization_level") ?? "" : "",
            capabilities);
    }

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static long Number(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.TryGetInt64(out var number)
            ? number
            : 0;

    // The "error" text of a reply, or the start of the reply when it has none.
    public static string ErrorIn(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("error", out var error))
                return error.ToString();
        }
        catch (JsonException) { }
        return body.Length > 300 ? body[..300] : body;
    }
}
