using System.Net;
using System.Text;
using System.Text.Json;

namespace ScribeWench.Tests.Support;

/// <summary>
/// Stands in for the internet: answers the addresses a test sets up, and refuses every other one
/// the way a machine with nothing listening does. Keeps what was asked.
/// </summary>
internal sealed class FakeWeb : HttpMessageHandler, IHttpClientFactory, IHttpMessageHandlerFactory
{
    public sealed record Request(string Method, string Url, string Body, string? Authorization)
    {
        public JsonElement Json => JsonDocument.Parse(Body).RootElement.Clone();
    }

    private readonly List<(string Method, string UrlStart, Func<Request, HttpResponseMessage> Respond)> _routes = [];

    public List<Request> Requests { get; } = [];

    public HttpClient CreateClient(string name) => new(this, disposeHandler: false);

    public HttpMessageHandler CreateHandler(string name) => this;

    /// <summary>Answers requests whose address starts with <paramref name="urlStart"/>. A later rule wins over an earlier one.</summary>
    public FakeWeb On(string method, string urlStart, Func<Request, HttpResponseMessage> respond)
    {
        _routes.Add((method, urlStart, respond));
        return this;
    }

    public FakeWeb On(string method, string urlStart, object json, HttpStatusCode status = HttpStatusCode.OK) =>
        On(method, urlStart, _ => Json(json, status));

    public static HttpResponseMessage Json(object json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json as string ?? JsonSerializer.Serialize(json), Encoding.UTF8, "application/json") };

    /// <summary>A reply made of one JSON object per line, as Ollama sends download progress.</summary>
    public static HttpResponseMessage Lines(params object[] lines) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(string.Join("\n", lines.Select(line => line as string ?? JsonSerializer.Serialize(line))) + "\n", Encoding.UTF8, "application/x-ndjson"),
        };

    public IEnumerable<Request> To(string urlPart) => Requests.Where(r => r.Url.Contains(urlPart, StringComparison.Ordinal));

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage message, CancellationToken ct)
    {
        var request = new Request(
            message.Method.Method,
            message.RequestUri!.ToString(),
            message.Content is null ? "" : await message.Content.ReadAsStringAsync(ct),
            message.Headers.Authorization?.ToString());
        Requests.Add(request);

        for (var i = _routes.Count - 1; i >= 0; i--)
            if (_routes[i].Method == request.Method && request.Url.StartsWith(_routes[i].UrlStart, StringComparison.Ordinal))
                return _routes[i].Respond(request);

        throw new HttpRequestException($"No connection could be made to {message.RequestUri.Host}");
    }
}
