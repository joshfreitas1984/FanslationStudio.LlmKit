using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace FanslationStudio.Installer.Core;

public record LatestRelease(string Version, string ZipName, string ZipUrl);

/// <summary>Reads the latest GitHub release anonymously. Never sends credentials.</summary>
public static class ReleaseClient
{
    /// <summary>Null when the repo has no releases yet.</summary>
    public static async Task<LatestRelease?> GetLatestAsync(HttpClient http, string ownerAndRepo, string zipPrefix,
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{ownerAndRepo}/releases/latest");
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("FanslationStudio-Installer", "1.0"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

        using var response = await http.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        response.EnsureSuccessStatusCode();

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var root = json.RootElement;

        var tag = root.GetProperty("tag_name").GetString() ?? throw new InvalidDataException("Release has no tag_name.");
        var prefix = zipPrefix + "-";

        foreach (var asset in root.GetProperty("assets").EnumerateArray())
        {
            var name = asset.GetProperty("name").GetString() ?? "";
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                return new LatestRelease(tag.TrimStart('v'), name, asset.GetProperty("browser_download_url").GetString()!);
        }

        throw new InvalidDataException($"Release {tag} has no '{prefix}*.zip' asset.");
    }

    /// <summary>Compares dotted numeric versions (yyyy.MM.dd.HH.mm). A missing installed version is always older.</summary>
    public static bool IsNewer(string? installed, string latest)
    {
        if (string.IsNullOrWhiteSpace(installed))
            return true;

        var a = Parse(installed);
        var b = Parse(latest);
        if (a == null || b == null)
            return !string.Equals(installed, latest, StringComparison.OrdinalIgnoreCase);

        for (var i = 0; i < Math.Max(a.Length, b.Length); i++)
        {
            var x = i < a.Length ? a[i] : 0;
            var y = i < b.Length ? b[i] : 0;
            if (x != y)
                return y > x;
        }

        return false;
    }

    static long[]? Parse(string version)
    {
        var parts = version.Trim().TrimStart('v').Split('.');
        var numbers = new long[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            if (!long.TryParse(parts[i], out numbers[i]))
                return null;
        }

        return numbers;
    }
}
