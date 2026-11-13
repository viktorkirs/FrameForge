using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace FrameForge.Core;

public sealed record AppUpdate(Version Version, Uri DownloadUri);

/// <summary>Reads only the small manifest published alongside the verified Windows installer.</summary>
public sealed class UpdateChecker(HttpClient? client = null)
{
    public static readonly Uri ManifestUri = new("https://github.com/viktorkirs/FrameForge/releases/download/latest/FrameForge-update.json");
    public static readonly Uri InstallerUri = new("https://github.com/viktorkirs/FrameForge/releases/download/latest/FrameForge-Setup-x64.exe");
    static readonly HttpClient DefaultClient = new() { Timeout = TimeSpan.FromSeconds(10) };
    readonly HttpClient _client = client ?? DefaultClient;
    const int MaximumManifestBytes = 16 * 1024;

    public async Task<AppUpdate?> CheckAsync(Version installed, CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        cancellationToken = timeout.Token;
        using var request = new HttpRequestMessage(HttpMethod.Get, ManifestUri);
        request.Headers.UserAgent.ParseAdd($"FrameForge/{Normalize(installed).ToString(3)}");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
        using HttpResponseMessage response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        byte[] buffer = new byte[MaximumManifestBytes + 1];
        int length = 0;
        while (length < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(length), cancellationToken);
            if (read == 0) break;
            length += read;
        }
        if (length > MaximumManifestBytes) throw new InvalidDataException("Update manifest is too large.");
        return ParseManifest(Encoding.UTF8.GetString(buffer, 0, length), installed);
    }

    public static AppUpdate? ParseManifest(string json, Version installed)
    {
        using JsonDocument manifest = JsonDocument.Parse(json);
        JsonElement root = manifest.RootElement;
        if (!root.TryGetProperty("version", out JsonElement value) || value.ValueKind != JsonValueKind.String ||
            !Version.TryParse(value.GetString(), out Version? version) || version.Build < 0)
            throw new InvalidDataException("Invalid update version.");
        if (!root.TryGetProperty("downloadUrl", out JsonElement url) || url.ValueKind != JsonValueKind.String ||
            !Uri.TryCreate(url.GetString(), UriKind.Absolute, out Uri? download) || download != InstallerUri)
            throw new InvalidDataException("Invalid installer URL.");
        version = Normalize(version);
        return version > Normalize(installed) ? new AppUpdate(version, InstallerUri) : null;
    }

    static Version Normalize(Version version) => new(version.Major, version.Minor, Math.Max(0, version.Build));
}
