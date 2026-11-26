namespace ScribeWench.Bot;

// Fetches the files people attach to a command. Throws HttpRequestException or TaskCanceledException when one can't be had.
public sealed class AttachmentDownloader(IHttpClientFactory factory)
{
    public async Task<byte[]> DownloadAsync(string url, TimeSpan timeout)
    {
        var http = factory.CreateClient("discord-attachments");
        http.Timeout = timeout;
        return await http.GetByteArrayAsync(url);
    }
}
