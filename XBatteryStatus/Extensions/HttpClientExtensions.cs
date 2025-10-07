namespace XBatteryStatus.Extensions;

/// <summary>
/// Extension methods for <see cref="HttpClient"/>.
/// </summary>
internal static class HttpClientExtensions
{
    public static async Task DownloadFileAsync(this HttpClient httpClient, Uri uri, string targetFile)
    {
        using var response = await httpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead);

        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync();

        await using var fileStream = File.Create(targetFile);

        await stream.CopyToAsync(fileStream);
    }
}
