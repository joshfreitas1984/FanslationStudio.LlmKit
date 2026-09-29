namespace FanslationStudio.Installer.Core;

public interface IFileDownloader
{
    Task DownloadAsync(string url, string destinationPath, CancellationToken cancellationToken = default);
}

public class HttpFileDownloader(HttpClient http) : IFileDownloader
{
    public async Task DownloadAsync(string url, string destinationPath, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destinationPath))!);

        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var target = File.Create(destinationPath);
        await source.CopyToAsync(target, cancellationToken);
    }
}
