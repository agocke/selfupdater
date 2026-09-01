using System.Net.Http.Headers;
using Serde;
using Serde.Json;

namespace SelfUpdater;

/// <summary>
/// Self-updater for apps distributed via a Forgejo (or Gitea) instance's Releases.
/// Lists the repo's releases through the instance's <c>/api/v1</c> REST API, selects
/// the asset for the configured platform (per <see cref="UpdaterOptions"/>), downloads
/// and verifies it, then hands off to the new binary to replace the running one in
/// place.
/// <para>
/// Unlike GitHub, Forgejo publishes no content digest for release attachments, so
/// integrity comes from a <b>sidecar asset</b>: upload <c>{assetName}.sha256</c>
/// alongside each build and it is fetched and used as the expected hash. This is the
/// same convention <see cref="DirectoryUpdater"/> uses, and the contents may be a bare
/// hash or the leading <c>sha256sum</c>-style <c>&lt;hash&gt;&#160;&#160;filename</c>
/// token. Without a sidecar the download is not checksum-verified. Sidecars are only
/// looked up for assets matching the default <c>{appName}-{version}-{rid}</c>
/// convention; supplying a custom <see cref="UpdaterOptions.Parser"/> disables the
/// lookup, since the naming it implies is unknown here.
/// </para>
/// <para>
/// Works with <b>private</b> repositories and instances that require auth to read at
/// all (a Tailscale-internal forge, say): supply an <c>authToken</c> and the API call
/// is authenticated with Forgejo's <c>Authorization: token …</c> scheme. The token is
/// fetched per request via a delegate so short-lived / rotating credentials refresh
/// automatically. It is sent on downloads only when the asset lives on the instance
/// itself — Forgejo release assets may instead be arbitrary external URLs, which must
/// not receive the instance's credentials.
/// </para>
/// <para>
/// Because <see cref="UpdaterOptions.AppName"/> drives the naming convention, two
/// binaries published to the same repo update independently: give one updater
/// <c>AppName = "myapp"</c> and another <c>AppName = "myapp-server"</c> and each sees
/// only its own assets, even when they ship in the same release and run on different
/// machines.
/// </para>
/// </summary>
public sealed class ForgejoUpdater : Updater
{
    private const string ChecksumExtension = ".sha256";

    /// <summary>
    /// Forgejo caps a list response at its <c>MAX_RESPONSE_ITEMS</c> setting (50 by
    /// default); asking for more than that gets silently clamped, so ask for exactly it.
    /// Only the newest release matters, and releases come back newest-first.
    /// </summary>
    private const int PageLimit = 50;

    private readonly Uri _instance;
    private readonly string _owner;
    private readonly string _repo;
    private readonly Func<CancellationToken, Task<string?>>? _authToken;
    private readonly HttpClient _http;

    /// <summary>The rid whose assets get a sidecar lookup, or null when a custom parser is in use.</summary>
    private readonly string? _sidecarRid;

    /// <param name="instance">
    /// Base URL of the Forgejo instance, e.g. <c>https://codeberg.org</c> or
    /// <c>https://forge.example.internal</c>. The <c>/api/v1</c> path is appended.
    /// </param>
    /// <param name="owner">Repository owner (user or org).</param>
    /// <param name="repo">Repository name.</param>
    /// <param name="options">Identity, version, platform, and swap settings.</param>
    /// <param name="authToken">
    /// Optional callback returning a Forgejo API token. Required for private repos (and
    /// for instances that require authentication to read); omit for public ones. Awaited
    /// once per request so a consumer can fetch/cache/refresh rotating credentials
    /// however it likes.
    /// </param>
    /// <param name="http">Optional pre-configured <see cref="HttpClient"/> (e.g. with proxy/handlers).</param>
    public ForgejoUpdater(
        Uri instance,
        string owner,
        string repo,
        UpdaterOptions options,
        Func<CancellationToken, Task<string?>>? authToken = null,
        HttpClient? http = null
    )
        : base(options)
    {
        _instance = instance;
        _owner = owner;
        _repo = repo;
        _authToken = authToken;
        _sidecarRid = options.Parser is null ? options.Rid : null;
        _http = http ?? new HttpClient();
        if (!_http.DefaultRequestHeaders.UserAgent.Any())
            _http.DefaultRequestHeaders.UserAgent.Add(
                new ProductInfoHeaderValue("selfupdater", "1.0")
            );
    }

    internal override async Task<IReadOnlyList<SourceAsset>> GetAssetsAsync(CancellationToken ct)
    {
        var url =
            $"{_instance.ToString().TrimEnd('/')}/api/v1/repos/{_owner}/{_repo}/releases?limit={PageLimit}";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        await AddAuthAsync(req, ct).ConfigureAwait(false);

        string json;
        try
        {
            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                return [];
            json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return [];
        }

        List<ForgejoRelease> releases;
        try
        {
            releases = JsonSerializer.Deserialize(json, List<ForgejoRelease>.Deserialize);
        }
        catch (Exception)
        {
            return [];
        }

        // Sidecars are assets themselves, so collect them by name first and pair them
        // up afterwards rather than assuming any particular order in the response.
        var sidecarUrls = new Dictionary<string, string>(StringComparer.Ordinal);
        var assets = new List<SourceAsset>();
        foreach (var release in releases)
        {
            if (release.Draft == true)
                continue;
            foreach (var a in release.Assets)
            {
                if (a.Name.EndsWith(ChecksumExtension, StringComparison.OrdinalIgnoreCase))
                {
                    sidecarUrls[a.Name] = a.BrowserDownloadUrl;
                    continue;
                }
                assets.Add(
                    new SourceAsset(
                        a.Name,
                        a.BrowserDownloadUrl,
                        Sha256: null,
                        a.Size,
                        release.Prerelease ?? false
                    )
                );
            }
        }

        return await AttachChecksumsAsync(assets, sidecarUrls, ct).ConfigureAwait(false);
    }

    internal override async Task<Stream> OpenAssetAsync(SourceAsset asset, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, asset.Location);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));
        await AddAuthAsync(req, ct).ConfigureAwait(false);

        var resp = await _http
            .SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Fill in <see cref="SourceAsset.Sha256"/> from each asset's <c>.sha256</c> sidecar.
    /// Only assets the default convention would keep for the configured rid are looked
    /// up — a sidecar costs a request each, and every other asset is about to be
    /// discarded by the naming filter anyway.
    /// </summary>
    private async Task<IReadOnlyList<SourceAsset>> AttachChecksumsAsync(
        List<SourceAsset> assets,
        Dictionary<string, string> sidecarUrls,
        CancellationToken ct
    )
    {
        if (_sidecarRid is null || sidecarUrls.Count == 0)
            return assets;

        var pending = new List<(int Index, Task<string?> Hash)>();
        for (var i = 0; i < assets.Count; i++)
        {
            if (!AssetNaming.MatchesRid(assets[i].Name, _sidecarRid))
                continue;
            if (!sidecarUrls.TryGetValue(assets[i].Name + ChecksumExtension, out var url))
                continue;
            pending.Add((i, FetchChecksumAsync(url, ct)));
        }

        foreach (var (index, hash) in pending)
        {
            if (await hash.ConfigureAwait(false) is { Length: > 0 } sha)
                assets[index] = assets[index] with { Sha256 = sha };
        }

        return assets;
    }

    /// <summary>
    /// Read a sidecar's hash, or null if it can't be fetched. A missing sidecar is not
    /// fatal: it leaves the asset unverified, exactly as if none had been published.
    /// </summary>
    private async Task<string?> FetchChecksumAsync(string url, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            await AddAuthAsync(req, ct).ConfigureAwait(false);
            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                return null;
            var text = (await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false)).Trim();
            if (text.Length == 0)
                return null;

            // Accept a bare hash or the "<hash>  filename" sha256sum format.
            var space = text.IndexOfAny([' ', '\t', '\n', '\r']);
            return space > 0 ? text[..space] : text;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>
    /// Attach the token, but only for requests to the instance itself: a Forgejo release
    /// asset can be an arbitrary external URL, which must not see these credentials.
    /// </summary>
    private async Task AddAuthAsync(HttpRequestMessage req, CancellationToken ct)
    {
        if (_authToken is null || req.RequestUri is not { } uri)
            return;
        if (!Uri.IsWellFormedUriString(uri.ToString(), UriKind.Absolute))
            return;
        if (
            !string.Equals(uri.Host, _instance.Host, StringComparison.OrdinalIgnoreCase)
            || uri.Port != _instance.Port
        )
            return;
        if (await _authToken(ct).ConfigureAwait(false) is { Length: > 0 } token)
            req.Headers.Authorization = new AuthenticationHeaderValue("token", token);
    }
}

[GenerateSerde]
internal sealed partial record ForgejoRelease
{
    [SerdeMemberOptions(Rename = "tag_name")]
    public required string TagName { get; init; }

    public bool? Draft { get; init; }

    public bool? Prerelease { get; init; }

    public required List<ForgejoAsset> Assets { get; init; }
}

[GenerateSerde]
internal sealed partial record ForgejoAsset
{
    public required string Name { get; init; }

    [SerdeMemberOptions(Rename = "browser_download_url")]
    public required string BrowserDownloadUrl { get; init; }

    public long? Size { get; init; }
}
