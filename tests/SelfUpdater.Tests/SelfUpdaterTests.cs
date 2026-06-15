using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Text;
using SelfUpdater;
using Semver;

namespace SelfUpdater.Tests;

public class SelfUpdaterTests
{
    [Theory]
    [InlineData("1.2.3", 1, 2, 3, "")]
    [InlineData("v0.1.0", 0, 1, 0, "")]
    [InlineData("2.0", 2, 0, 0, "")]
    [InlineData("1.2.3-beta.1+build7", 1, 2, 3, "beta.1")]
    public void Version_ParsesLenientForms(string text, int major, int minor, int patch, string pre)
    {
        Assert.True(SemVersion.TryParse(text, SemVersionStyles.Any, out var v));
        Assert.Equal(major, v.Major);
        Assert.Equal(minor, v.Minor);
        Assert.Equal(patch, v.Patch);
        Assert.Equal(pre, v.Prerelease);
        Assert.Equal(pre.Length > 0, v.IsPrerelease);
    }

    [Theory]
    [InlineData("garbage")]
    [InlineData("1.2.3.4")]
    [InlineData("1.-2.3")]
    [InlineData("")]
    public void Version_RejectsInvalid(string text) =>
        Assert.False(SemVersion.TryParse(text, SemVersionStyles.Any, out _));

    [Fact]
    public void Version_OrdersByPrecedence()
    {
        Assert.True(V("1.0.0").ComparePrecedenceTo(V("1.0.1")) < 0);
        Assert.True(V("1.2.0").ComparePrecedenceTo(V("1.1.9")) > 0);
        // Prerelease sorts below its release.
        Assert.True(V("1.0.0-beta").ComparePrecedenceTo(V("1.0.0")) < 0);
        Assert.True(V("1.0.0-alpha").ComparePrecedenceTo(V("1.0.0-beta")) < 0);
        Assert.True(V("1.0.0-alpha.1").ComparePrecedenceTo(V("1.0.0-alpha.2")) < 0);
        // Build metadata does not affect precedence.
        Assert.Equal(0, V("1.0.0+a").ComparePrecedenceTo(V("1.0.0+b")));
    }

    [Fact]
    public async Task GitHubSource_ListsReleasesForSelectedRid()
    {
        const string json = """
            [
              {
                "tag_name": "v0.3.0",
                "html_url": "https://github.com/agocke/app/releases/tag/v0.3.0",
                "assets": [
                  { "name": "app-0.3.0-osx-arm64", "browser_download_url": "https://dl/osx-030", "size": 42,
                    "digest": "sha256:DEADBEEF" },
                  { "name": "app-0.3.0-linux-x64", "browser_download_url": "https://dl/linux-030" }
                ]
              },
              {
                "tag_name": "v0.2.0",
                "assets": [
                  { "name": "app-0.2.0-osx-arm64", "browser_download_url": "https://dl/osx-020" }
                ]
              }
            ]
            """;
        var updater = new GitHubUpdater("agocke", "app", Options("0.0.0"), http: StubClient(json));

        var releases = await updater.GetReleasesAsync();

        Assert.Equal(2, releases.Count);
        var v030 = releases.Single(r => r.Version == V("0.3.0"));
        // Only the selected rid's asset is surfaced; the linux build is filtered out.
        var osx = v030.Asset;
        Assert.Equal("app-0.3.0-osx-arm64", osx.Name);
        Assert.Equal("https://dl/osx-030", osx.Location);
        Assert.Equal(42, osx.Size);
        // GitHub's published digest becomes the integrity hash.
        Assert.Equal("DEADBEEF", osx.Sha256);
    }

    [Fact]
    public async Task DefaultConvention_DisambiguatesDashedVersionAndRid()
    {
        // Both the prerelease version (1.2.3-beta.1) and the rid (osx-arm64) contain
        // dashes; knowing the target rid is what makes the split unambiguous.
        const string json = """
            [
              {
                "tag_name": "v1.2.3-beta.1",
                "assets": [
                  { "name": "app-1.2.3-beta.1-osx-arm64", "browser_download_url": "https://dl/osx" },
                  { "name": "app-1.2.3-beta.1-linux-x64", "browser_download_url": "https://dl/linux" }
                ]
              }
            ]
            """;
        var updater = new GitHubUpdater("agocke", "app", Options("0.0.0"), http: StubClient(json));

        var release = Assert.Single(await updater.GetReleasesAsync());

        Assert.Equal(V("1.2.3-beta.1"), release.Version);
        Assert.True(release.IsPrerelease);
        Assert.Equal("app-1.2.3-beta.1-osx-arm64", release.Asset.Name);
        Assert.Equal("https://dl/osx", release.Asset.Location);
    }

    [Fact]
    public async Task GitHubSource_SkipsDraftsAndFlagsPrereleases()
    {
        const string json = """
            [
              { "tag_name": "v0.4.0", "draft": true,
                "assets": [ { "name": "app-0.4.0-osx-arm64", "browser_download_url": "https://dl/draft" } ] },
              { "tag_name": "v0.3.0", "prerelease": true,
                "assets": [ { "name": "app-0.3.0-osx-arm64", "browser_download_url": "https://dl/pre" } ] },
              { "tag_name": "v0.2.0",
                "assets": [ { "name": "app-0.2.0-osx-arm64", "browser_download_url": "https://dl/rel" } ] }
            ]
            """;
        var updater = new GitHubUpdater("agocke", "app", Options("0.0.0"), http: StubClient(json));

        var releases = await updater.GetReleasesAsync();

        // The draft is gone; the prerelease is present and flagged.
        Assert.Equal(2, releases.Count);
        Assert.DoesNotContain(releases, r => r.Version == V("0.4.0"));
        Assert.True(releases.Single(r => r.Version == V("0.3.0")).IsPrerelease);
        Assert.False(releases.Single(r => r.Version == V("0.2.0")).IsPrerelease);
    }

    [Fact]
    public async Task DirectorySource_ListsBinariesByConventionAndOpensAsset()
    {
        var dir = Directory.CreateTempSubdirectory("selfupdater-test-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(dir, "myapp-1.5.0-osx-arm64"), "the-binary-bytes");
            File.WriteAllText(Path.Combine(dir, "myapp-1.5.0-linux-x64"), "linux-bytes");
            // Sidecar checksum (sha256sum format) is picked up for the osx binary only.
            File.WriteAllText(
                Path.Combine(dir, "myapp-1.5.0-osx-arm64.sha256"),
                "abc123  myapp-1.5.0-osx-arm64\n"
            );
            // Unrelated files are ignored.
            File.WriteAllText(Path.Combine(dir, "notes.txt"), "ignore me");

            var updater = new DirectoryUpdater(dir, Options("0.0.0", appName: "myapp"));
            var releases = await updater.GetReleasesAsync();
            var release = Assert.Single(releases);

            Assert.Equal(V("1.5.0"), release.Version);
            // Only the selected rid's binary is surfaced; the linux build is filtered out.
            var osx = release.Asset;
            Assert.Equal("myapp-1.5.0-osx-arm64", osx.Name);
            Assert.Equal(Path.Combine(dir, "myapp-1.5.0-osx-arm64"), osx.Location);
            Assert.Equal("abc123", osx.Sha256);

            await using var stream = await updater.OpenAssetAsync(osx, default);
            using var reader = new StreamReader(stream);
            Assert.Equal("the-binary-bytes", await reader.ReadToEndAsync());
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task DirectorySource_GroupsReleasesAndFlagsPrereleasesViaCustomParser()
    {
        var dir = Directory.CreateTempSubdirectory("selfupdater-test-").FullName;
        try
        {
            // A non-default scheme (underscore separators) handled by a custom parser.
            File.WriteAllText(Path.Combine(dir, "myapp_2.0.0_osx-arm64"), "stable");
            File.WriteAllText(Path.Combine(dir, "myapp_3.0.0-rc.1_osx-arm64"), "rc");

            var options = Options("0.0.0", appName: "myapp") with
            {
                Parser = fileName =>
                {
                    const string prefix = "myapp_";
                    const string suffix = "_osx-arm64";
                    if (!fileName.StartsWith(prefix) || !fileName.EndsWith(suffix))
                        return null;
                    var version = fileName[prefix.Length..^suffix.Length];
                    return SemVersion.TryParse(version, SemVersionStyles.Any, out var v)
                        ? (v, "osx-arm64")
                        : null;
                },
            };
            var updater = new DirectoryUpdater(dir, options);

            var releases = await updater.GetReleasesAsync();

            Assert.Equal(2, releases.Count);
            Assert.True(releases.Single(r => r.Version == V("3.0.0-rc.1")).IsPrerelease);
            Assert.False(releases.Single(r => r.Version == V("2.0.0")).IsPrerelease);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task DirectorySource_MissingDirectoryReturnsEmpty()
    {
        var dir = Path.Combine(
            Path.GetTempPath(),
            "selfupdater-missing-" + Path.GetRandomFileName()
        );
        var updater = new DirectoryUpdater(dir, Options("0.0.0", appName: "myapp"));
        Assert.Empty(await updater.GetReleasesAsync());
    }

    [Fact]
    public async Task Updater_DetectsAvailableAndUpToDate()
    {
        var available = await new FakeUpdater(Options("1.0.0"), RawAsset("9.9.9")).FetchAsync();
        Assert.NotNull(available);
        Assert.Equal(V("9.9.9"), available.Version);

        var upToDate = await new FakeUpdater(Options("1.0.0"), RawAsset("1.0.0")).FetchAsync();
        Assert.Null(upToDate);
    }

    [Fact]
    public async Task Updater_PicksNewestAcrossMultipleReleases()
    {
        var fetched = await new FakeUpdater(
            Options("1.5.0"),
            RawAsset("1.0.0"),
            RawAsset("3.0.0"),
            RawAsset("2.0.0")
        ).FetchAsync();

        Assert.NotNull(fetched);
        Assert.Equal(V("3.0.0"), fetched.Version);
    }

    [Fact]
    public async Task Updater_ReleaseFilterExcludesPrereleases()
    {
        var assets = new[] { RawAsset("2.0.0"), RawAsset("3.0.0-rc.1") };

        // Without a filter the prerelease is newest; with one it is skipped.
        var all = await new FakeUpdater(Options("1.0.0"), assets).FetchAsync();
        Assert.Equal(V("3.0.0-rc.1"), all!.Version);

        var stableOnly = await new FakeUpdater(
            Options("1.0.0", releaseFilter: r => !r.IsPrerelease),
            assets
        ).FetchAsync();
        Assert.Equal(V("2.0.0"), stableOnly!.Version);
    }

    [Fact]
    public async Task Updater_NoUpdateWhenNoAssetForPlatform()
    {
        // A newer build exists, but only for another platform: nothing to fetch.
        var fetched = await new FakeUpdater(
            Options("1.0.0"),
            RawAsset("2.0.0", rid: "win-x64")
        ).FetchAsync();
        Assert.Null(fetched);
    }

    [Fact]
    public async Task Updater_UpToDateWhenNoNewerRelease()
    {
        var result = await new FakeUpdater(
            Options("1.0.0", allowNonSingleFile: true),
            RawAsset("1.0.0")
        ).UpdateAsync();
        Assert.Equal(UpdateOutcome.UpToDate, result.Outcome);
    }

    [Fact]
    public async Task GitHubSource_PrivateRepoUsesAuthAndAssetApiUrl()
    {
        const string json = """
            [
              {
                "tag_name": "v0.3.0",
                "assets": [
                  {
                    "name": "app-0.3.0-osx-arm64",
                    "url": "https://api.github.com/repos/agocke/app/releases/assets/99",
                    "browser_download_url": "https://dl/osx",
                    "size": 42
                  }
                ]
              }
            ]
            """;
        string? seenAuth = null;
        string? seenAccept = null;
        var handler = new CapturingHandler(
            json,
            req =>
            {
                seenAuth = req.Headers.Authorization?.ToString();
                seenAccept = req.Headers.Accept.ToString();
            }
        );
        var source = new GitHubUpdater(
            "agocke",
            "app",
            Options("0.0.0"),
            authToken: _ => Task.FromResult<string?>("tok-123"),
            http: new HttpClient(handler)
        );

        var asset = Assert.Single(await source.GetAssetsAsync(default));

        // Private repos download via the asset API url, not browser_download_url.
        Assert.Equal("https://api.github.com/repos/agocke/app/releases/assets/99", asset.Location);
        Assert.Equal("Bearer tok-123", seenAuth);

        await using var _ = await source.OpenAssetAsync(asset, default);
        Assert.Contains("application/octet-stream", seenAccept);
        Assert.Equal("Bearer tok-123", seenAuth);
    }

    [Fact]
    public async Task DefaultConvention_AcceptsArchiveExtensions()
    {
        // Multi-file releases ship as archives; the default convention strips a known
        // archive extension before matching {appName}-{version}-{rid}.
        const string json = """
            [
              {
                "tag_name": "v1.2.3",
                "assets": [
                  { "name": "app-1.2.3-osx-arm64.zip", "browser_download_url": "https://dl/osx" },
                  { "name": "app-1.2.3-linux-x64.tar.gz", "browser_download_url": "https://dl/linux" }
                ]
              }
            ]
            """;
        var updater = new GitHubUpdater("agocke", "app", Options("0.0.0"), http: StubClient(json));

        var release = Assert.Single(await updater.GetReleasesAsync());

        Assert.Equal(V("1.2.3"), release.Version);
        // The osx archive is selected for the configured rid; its full name is preserved.
        Assert.Equal("app-1.2.3-osx-arm64.zip", release.Asset.Name);
        Assert.Equal("https://dl/osx", release.Asset.Location);
    }

    [Fact]
    public void ApplySwap_DirectoryMode_ReplacesTreeDropsStaleAndPreservesModes()
    {
        var root = Directory.CreateTempSubdirectory("selfupdater-swap-").FullName;
        try
        {
            var dest = Path.Combine(root, "install", "MyApp.app");
            var staged = Path.Combine(root, "staged", "MyApp.app");

            // Old install: an executable, a plist, and a stale file the update drops.
            WriteFile(Path.Combine(dest, "Contents", "MacOS", "app"), "old", executable: true);
            WriteFile(Path.Combine(dest, "Contents", "Info.plist"), "old-plist");
            WriteFile(Path.Combine(dest, "Contents", "MacOS", "stale.txt"), "stale");

            // Staged build: new exe contents, a new resource, and no stale file.
            WriteFile(Path.Combine(staged, "Contents", "MacOS", "app"), "new", executable: true);
            WriteFile(Path.Combine(staged, "Contents", "Info.plist"), "new-plist");
            WriteFile(Path.Combine(staged, "Contents", "Resources", "data.bin"), "data");

            var code = Updater.ApplySwap(
                dest,
                oldPid: 0,
                relaunchArgs: null,
                sourceDir: staged,
                log: TextWriter.Null
            );

            Assert.Equal(0, code);
            Assert.Equal("new", File.ReadAllText(Path.Combine(dest, "Contents", "MacOS", "app")));
            Assert.Equal(
                "new-plist",
                File.ReadAllText(Path.Combine(dest, "Contents", "Info.plist"))
            );
            Assert.True(File.Exists(Path.Combine(dest, "Contents", "Resources", "data.bin")));
            // Files present only in the old tree are gone after the swap.
            Assert.False(File.Exists(Path.Combine(dest, "Contents", "MacOS", "stale.txt")));
            // No backup directory left behind on success.
            Assert.False(Directory.Exists(dest + ".bak"));

            if (!OperatingSystem.IsWindows())
            {
                var mode = File.GetUnixFileMode(Path.Combine(dest, "Contents", "MacOS", "app"));
                Assert.True(mode.HasFlag(UnixFileMode.UserExecute));
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("MyApp-1.2.3-osx-arm64.zip")]
    [InlineData("MyApp-1.2.3-osx-arm64.tar.gz")]
    [InlineData("MyApp-1.2.3-osx-arm64.tgz")]
    public void ExtractArchive_DispatchesOnExtension_PreservingTreeAndModes(string archiveName)
    {
        var root = Directory.CreateTempSubdirectory("selfupdater-extract-").FullName;
        try
        {
            // A bundle-like source tree with an executable, a nested resource, and a plist.
            var src = Path.Combine(root, "MyApp.app");
            WriteFile(Path.Combine(src, "Contents", "MacOS", "app"), "bin", executable: true);
            WriteFile(Path.Combine(src, "Contents", "Info.plist"), "plist");
            WriteFile(Path.Combine(src, "Contents", "Resources", "data.bin"), "data");

            var archive = Path.Combine(root, archiveName);
            if (archiveName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                ZipFile.CreateFromDirectory(
                    src,
                    archive,
                    CompressionLevel.Optimal,
                    includeBaseDirectory: true
                );
            }
            else
            {
                using var file = File.Create(archive);
                using var gzip = new GZipStream(file, CompressionLevel.Optimal);
                TarFile.CreateFromDirectory(src, gzip, includeBaseDirectory: true);
            }

            var extractDir = Path.Combine(root, "extracted");
            Updater.ExtractArchive(archive, extractDir);

            // Single top-level directory (the bundle), with all files intact.
            var top = Path.Combine(extractDir, "MyApp.app");
            Assert.Equal("bin", File.ReadAllText(Path.Combine(top, "Contents", "MacOS", "app")));
            Assert.Equal("plist", File.ReadAllText(Path.Combine(top, "Contents", "Info.plist")));
            Assert.True(File.Exists(Path.Combine(top, "Contents", "Resources", "data.bin")));

            if (!OperatingSystem.IsWindows())
            {
                var mode = File.GetUnixFileMode(Path.Combine(top, "Contents", "MacOS", "app"));
                Assert.True(mode.HasFlag(UnixFileMode.UserExecute));
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void WriteFile(string path, string content, bool executable = false)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        if (executable && !OperatingSystem.IsWindows())
        {
            var mode =
                File.GetUnixFileMode(path)
                | UnixFileMode.UserExecute
                | UnixFileMode.GroupExecute
                | UnixFileMode.OtherExecute;
            File.SetUnixFileMode(path, mode);
        }
    }

    private static SemVersion V(string value) => SemVersion.Parse(value, SemVersionStyles.Any);

    private static SourceAsset RawAsset(
        string version,
        string rid = "osx-arm64",
        string app = "app"
    ) => new($"{app}-{version}-{rid}", $"loc-{version}-{rid}");

    private static UpdaterOptions Options(
        string current,
        string appName = "app",
        string rid = "osx-arm64",
        Func<Release, bool>? releaseFilter = null,
        bool allowNonSingleFile = false
    ) =>
        new()
        {
            AppName = appName,
            CurrentVersion = V(current),
            Rid = rid,
            ReleaseFilter = releaseFilter,
            Relaunch = false,
            AllowNonSingleFile = allowNonSingleFile,
            Log = TextWriter.Null,
        };

    private static HttpClient StubClient(string body) => new(new StubHandler(body));

    private sealed class StubHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        ) =>
            Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json"),
                }
            );
    }

    private sealed class CapturingHandler(string body, Action<HttpRequestMessage> capture)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            capture(request);
            return Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json"),
                }
            );
        }
    }

    // A test-only Updater whose "source" is an in-memory asset list, used to exercise
    // the shared engine logic (naming, selection, newest-wins) without HTTP or disk.
    private sealed class FakeUpdater : Updater
    {
        private readonly IReadOnlyList<SourceAsset> _assets;

        public FakeUpdater(UpdaterOptions options, params SourceAsset[] assets)
            : base(options)
        {
            _assets = assets;
        }

        internal override Task<IReadOnlyList<SourceAsset>> GetAssetsAsync(CancellationToken ct) =>
            Task.FromResult(_assets);

        internal override Task<Stream> OpenAssetAsync(SourceAsset asset, CancellationToken ct) =>
            Task.FromResult<Stream>(new MemoryStream());
    }
}
