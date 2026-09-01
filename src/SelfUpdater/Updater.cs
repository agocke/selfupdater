using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.InteropServices;
using Semver;

namespace SelfUpdater;

/// <summary>
/// A raw, source-provided build artifact, identified by the publisher's own
/// <see cref="Name"/> (a GitHub asset file name, a directory file name, ...). The
/// name is interpreted only by the updater's naming convention (see
/// <see cref="AssetNameParser"/>). <see cref="Location"/> is opaque to the shared
/// engine and interpreted only by the updater that produced it (a URL for
/// <see cref="GitHubUpdater"/>, a file path for <see cref="DirectoryUpdater"/>).
/// </summary>
/// <param name="Name">The source's own asset name, fed to the naming convention.</param>
/// <param name="Location">Where the asset can be opened from (path or URL).</param>
/// <param name="Sha256">Optional integrity hash, if the source published one.</param>
/// <param name="Size">Optional asset size in bytes, if known.</param>
/// <param name="IsPrerelease">
/// A source-side prerelease signal (e.g. GitHub's <c>prerelease</c> flag) OR-ed into
/// the resulting release's <see cref="Release.IsPrerelease"/>, on top of whatever the
/// parsed version itself indicates.
/// </param>
public sealed record SourceAsset(
    string Name,
    string Location,
    string? Sha256 = null,
    long? Size = null,
    bool IsPrerelease = false
);

/// <summary>A release for the configured platform, parsed from a source's assets.</summary>
/// <param name="Version">The release version, parsed from the asset's name.</param>
/// <param name="Asset">The raw asset for the configured <see cref="UpdaterOptions.Rid"/>.</param>
/// <param name="IsPrerelease">
/// True when the publisher marked this a prerelease (GitHub's <c>prerelease</c> flag,
/// or a SemVer prerelease suffix). Surfaced so a consumer can choose to skip
/// prereleases via <see cref="UpdaterOptions.ReleaseFilter"/>.
/// </param>
public sealed record Release(SemVersion Version, SourceAsset Asset, bool IsPrerelease = false);

public enum UpdateOutcome
{
    /// <summary>Already on the latest version (or newer).</summary>
    UpToDate,

    /// <summary>A newer build was downloaded, validated, and handed off; the caller should now exit.</summary>
    Staged,

    /// <summary>The running process is not a self-contained single file, so it cannot replace itself.</summary>
    NotSelfContained,

    /// <summary>The update could not be completed (network, checksum, validation, ...).</summary>
    Failed,
}

public sealed record UpdateResult(
    UpdateOutcome Outcome,
    SemVersion? Version = null,
    string? Message = null
);

/// <summary>
/// Everything an updater needs: the app's identity and version, the platform to
/// target, how asset names map to releases, and how to stage the swap. Build one up,
/// hand it to a concrete updater (e.g. <c>new GitHubUpdater(owner, repo, options)</c>),
/// then call <c>UpdateAsync</c> (or <c>FetchAsync</c> + <c>ApplyAsync</c>).
/// </summary>
public sealed record UpdaterOptions
{
    /// <summary>
    /// Application name for the default <c>{appName}-{version}-{rid}</c> naming
    /// convention. Ignored when a custom <see cref="Parser"/> is supplied.
    /// </summary>
    public required string AppName { get; init; }

    /// <summary>
    /// The version the app is currently running. The updater never fetches this for
    /// you; you own it. A release is "newer" when its version has higher precedence.
    /// </summary>
    public required SemVersion CurrentVersion { get; init; }

    /// <summary>
    /// Target runtime identifier (e.g. <c>osx-arm64</c>): the suffix the default
    /// convention selects on, and what makes the otherwise-ambiguous
    /// <c>{version}-{rid}</c> tail splittable. Defaults to the running platform's
    /// <see cref="RuntimeInformation.RuntimeIdentifier"/>.
    /// </summary>
    public string Rid { get; init; } = RuntimeInformation.RuntimeIdentifier;

    /// <summary>
    /// Optional override for how a raw asset name maps to <c>(version, rid)</c>. When
    /// omitted the default <c>{appName}-{version}-{rid}</c> convention is used. Only
    /// assets whose mapped rid equals <see cref="Rid"/> are kept.
    /// </summary>
    public AssetNameParser? Parser { get; init; }

    /// <summary>
    /// Optional predicate restricting which releases are considered (e.g.
    /// <c>r =&gt; !r.IsPrerelease</c> to ignore prereleases). When <c>null</c> every
    /// release for the platform is a candidate.
    /// </summary>
    public Func<Release, bool>? ReleaseFilter { get; init; }

    /// <summary>Path of the binary to replace. Defaults to the running executable.</summary>
    public string? TargetPath { get; init; }

    /// <summary>
    /// Opt into <b>versioned (multi-file) installs</b> — for apps that are not a single
    /// file, e.g. a binary shipping sidecar native assets. When set, the release asset is
    /// treated as a <c>.zip</c> or <c>.tar.gz</c>/<c>.tgz</c> archive, and this path is the
    /// install root laid out as:
    /// <code>
    /// &lt;InstallRoot&gt;/
    ///   current.version      pointer file naming the active version
    ///   current              symlink to versions/&lt;active&gt; (POSIX only, best effort)
    ///   versions/1.2.3/      the running build; never touched by an update
    ///   versions/1.2.4/      freshly unpacked
    /// </code>
    /// An update unpacks into a new <c>versions/</c> directory that nothing points at and
    /// then replaces the pointer file, so the live install is never mutated: an
    /// interrupted update leaves a junk directory and a working app. Nothing in use is
    /// renamed or deleted, which is also what makes this work on Windows.
    /// <para>
    /// The running executable must live inside a version directory (directly, or via
    /// <c>current</c>); its location relative to that directory is reused to find the
    /// executable in the staged build. Use <see cref="Updater.ResolveCurrent"/> to
    /// resolve the active version's directory at launch. When <c>null</c> (the default)
    /// the updater swaps a single file.
    /// </para>
    /// </summary>
    public string? InstallRoot { get; init; }

    /// <summary>
    /// Arguments used to smoke-test a freshly downloaded binary, which must exit 0.
    /// Validation is <b>opt-in</b>: when <c>null</c> or empty (the default) the
    /// downloaded binary is not executed before being staged. Set this only if your
    /// binary supports the given arguments and exits 0 on success (e.g.
    /// <c>["--version"]</c>) — there is no universally safe probe to assume.
    /// </summary>
    public IReadOnlyList<string>? ValidateArgs { get; init; }

    /// <summary>When true, the handed-off process relaunches the app after swapping.</summary>
    public bool Relaunch { get; init; }

    /// <summary>Allow updating even when not deployed as a single file (e.g. for tests).</summary>
    public bool AllowNonSingleFile { get; init; }

    public TextWriter Log { get; init; } = Console.Out;
}

/// <summary>
/// Source-agnostic self-update engine, modeled on dnvm. A concrete updater
/// (<see cref="GitHubUpdater"/>, <see cref="DirectoryUpdater"/>) supplies just two
/// things — how to list a source's raw assets and how to open one's bytes — and this
/// base does the rest: turn asset names into releases for the configured platform,
/// pick the newest, download and verify it, then hand off to the new binary which
/// replaces the old one in place (a two-process swap so a running executable can
/// update itself, including on Windows).
/// <para>
/// The caller owns its current version (<see cref="UpdaterOptions.CurrentVersion"/>);
/// the engine owns naming, platform selection, and the "newest wins" comparison.
/// </para>
/// </summary>
public abstract class Updater
{
    // Wire contract for the handoff (new-process) side. The host app registers a
    // command/handler with these exact names; keeping them here makes this the
    // single source of truth shared by both processes.
    public const string HandoffVerb = "apply-update";
    public const string DestOption = "--dest";
    public const string PidOption = "--pid";
    public const string RelaunchOption = "--relaunch";

    private readonly UpdaterOptions _options;
    private readonly AssetNameParser _parse;
    private readonly TextWriter _log;

    protected Updater(UpdaterOptions options)
    {
        _options = options;
        _parse = options.Parser ?? AssetNaming.DefaultParser(options.AppName, options.Rid);
        _log = options.Log;
    }

    /// <summary>
    /// List every raw artifact this source can see, with whatever metadata it knows
    /// (location, integrity hash, size, prerelease hint). Returns an empty list if the
    /// source could not be reached or parsed. Order is not significant.
    /// </summary>
    internal abstract Task<IReadOnlyList<SourceAsset>> GetAssetsAsync(CancellationToken ct);

    /// <summary>Open a read stream over an asset's bytes for downloading.</summary>
    internal abstract Task<Stream> OpenAssetAsync(SourceAsset asset, CancellationToken ct);

    /// <summary>
    /// List the releases available for the configured platform, with asset names
    /// parsed into versions and filtered to <see cref="UpdaterOptions.Rid"/>. A
    /// naming/selection testing seam; applies no version comparison and no
    /// <see cref="UpdaterOptions.ReleaseFilter"/>.
    /// </summary>
    internal async Task<IReadOnlyList<Release>> GetReleasesAsync(CancellationToken ct = default)
    {
        var assets = await GetAssetsAsync(ct).ConfigureAwait(false);
        return AssetNaming.ToReleases(assets, _parse, _options.Rid);
    }

    /// <summary>
    /// Resolve the exact release this updater would move to: the newest release for the
    /// configured platform (after <see cref="UpdaterOptions.ReleaseFilter"/>) whose
    /// version is newer than <see cref="UpdaterOptions.CurrentVersion"/>. Returns
    /// <c>null</c> when nothing applies — already up to date, or the source has no build
    /// for this platform. This is the network round-trip; hand the result to
    /// <see cref="ApplyAsync"/> to download and stage it (or just call
    /// <see cref="UpdateAsync"/>, which does both).
    /// </summary>
    public async Task<Release?> FetchAsync(CancellationToken ct = default)
    {
        var current = _options.CurrentVersion;
        _log.WriteLine($"Checking for updates (current {current})...");
        var assets = await GetAssetsAsync(ct).ConfigureAwait(false);
        var releases = AssetNaming.ToReleases(assets, _parse, _options.Rid);
        var newest = Newest(releases, _options.ReleaseFilter);
        if (newest is null)
        {
            if (assets.Count > 0)
                _log.WriteLine($"Source has builds, but none for {_options.Rid}.");
            else
                _log.WriteLine("Update source returned no release information.");
            return null;
        }

        if (newest.Version.ComparePrecedenceTo(current) <= 0)
        {
            _log.WriteLine($"Up to date (latest {newest.Version}).");
            return null;
        }

        _log.WriteLine($"Update available: {newest.Version}");
        return newest;
    }

    /// <summary>
    /// Download, validate, and stage a release resolved by <see cref="FetchAsync"/>,
    /// then hand off to the new binary. Applies exactly the given release — it does no
    /// version comparison of its own.
    /// </summary>
    public async Task<UpdateResult> ApplyAsync(Release release, CancellationToken ct = default)
    {
        if (!_options.AllowNonSingleFile && !Utilities.IsSingleFile())
        {
            _log.WriteLine(
                "Cannot self-update: not deployed as a single file (e.g. running via 'dotnet run')."
            );
            return new UpdateResult(UpdateOutcome.NotSelfContained, release.Version);
        }

        return _options.InstallRoot is { Length: > 0 } installRoot
            ? await ApplyVersionedAsync(release, installRoot, ct).ConfigureAwait(false)
            : await ApplyFileAsync(release, ct).ConfigureAwait(false);
    }

    /// <summary>Single-file swap: download the binary, validate it, hand off to replace the target file.</summary>
    private async Task<UpdateResult> ApplyFileAsync(Release release, CancellationToken ct)
    {
        var target = _options.TargetPath ?? Utilities.ProcessPath;
        if (string.IsNullOrEmpty(target))
        {
            _log.WriteLine("Cannot self-update: unable to determine the target executable path.");
            return new UpdateResult(UpdateOutcome.Failed, release.Version, "Unknown target path.");
        }

        var staged = await DownloadAndValidateAsync(release.Asset, target, ct)
            .ConfigureAwait(false);
        if (staged is null)
            return new UpdateResult(
                UpdateOutcome.Failed,
                release.Version,
                "Download or validation failed."
            );

        if (!LaunchHandoff(staged, target))
            return new UpdateResult(
                UpdateOutcome.Failed,
                release.Version,
                "Could not launch the handoff process."
            );

        return new UpdateResult(UpdateOutcome.Staged, release.Version);
    }

    /// <summary>
    /// Versioned (multi-file) install: download and extract the archive into a new
    /// <c>versions/</c> directory, validate it, then move the pointer. The live install
    /// is never touched, so nothing here needs to be undone if it fails partway.
    /// </summary>
    private async Task<UpdateResult> ApplyVersionedAsync(
        Release release,
        string installRoot,
        CancellationToken ct
    )
    {
        installRoot = Path.GetFullPath(installRoot);

        // Where the executable sits inside a version directory ("app", "bin/app", ...).
        // Reused to find the executable in the staged build and to relaunch it.
        var exePath = _options.TargetPath ?? Utilities.ProcessPath;
        var relExe = RelativeExePath(installRoot, exePath);
        if (relExe is null)
        {
            _log.WriteLine(
                $"Cannot self-update: the running executable is not inside a version directory under {installRoot}."
            );
            return new UpdateResult(
                UpdateOutcome.Failed,
                release.Version,
                "Executable is outside the install root."
            );
        }

        var versionsDir = Path.Combine(installRoot, VersionsDirName);
        var versionDir = Path.Combine(versionsDir, release.Version.ToString());
        if (Directory.Exists(versionDir))
        {
            // A previous attempt at this version left something behind. It cannot be the
            // running build (that version would not have been fetched), so it is junk.
            _log.WriteLine($"Discarding an incomplete earlier attempt at {release.Version}.");
            try
            {
                Directory.Delete(versionDir, recursive: true);
            }
            catch (Exception e)
            {
                _log.WriteLine($"Could not clear {versionDir}: {e.Message}");
                return new UpdateResult(
                    UpdateOutcome.Failed,
                    release.Version,
                    "Stale version directory."
                );
            }
        }

        // Unpack into a staging directory alongside the version directories, so moving
        // the build into place is a same-volume rename rather than a copy.
        Directory.CreateDirectory(versionsDir);
        var staging = Path.Combine(versionsDir, StagingPrefix + Path.GetRandomFileName());
        try
        {
            var buildRoot = await DownloadAndExtractAsync(release.Asset, staging, ct)
                .ConfigureAwait(false);
            if (buildRoot is null)
                return new UpdateResult(
                    UpdateOutcome.Failed,
                    release.Version,
                    "Download or extraction failed."
                );

            Directory.Move(buildRoot, versionDir);
        }
        catch (Exception e)
        {
            _log.WriteLine($"Could not stage {release.Version}: {e.Message}");
            return new UpdateResult(UpdateOutcome.Failed, release.Version, "Staging failed.");
        }
        finally
        {
            TryDelete(staging);
        }

        var stagedExe = Path.Combine(versionDir, relExe);
        if (!File.Exists(stagedExe))
        {
            _log.WriteLine($"Staged build does not contain the expected executable ({relExe}).");
            TryDelete(versionDir);
            return new UpdateResult(
                UpdateOutcome.Failed,
                release.Version,
                "Staged build is missing the executable."
            );
        }
        if (!OperatingSystem.IsWindows())
            Utilities.MakeExecutable(stagedExe);

        if (!await ValidateAsync(stagedExe, ct).ConfigureAwait(false))
        {
            TryDelete(versionDir);
            return new UpdateResult(UpdateOutcome.Failed, release.Version, "Validation failed.");
        }

        // The one operation that has to be atomic, and the only one that changes what
        // the app resolves to. Everything above this line is invisible to the install.
        if (!SetCurrent(installRoot, release.Version.ToString(), _log))
        {
            TryDelete(versionDir);
            return new UpdateResult(
                UpdateOutcome.Failed,
                release.Version,
                "Could not update the version pointer."
            );
        }

        _log.WriteLine($"Installed {release.Version}; {CurrentFileName} now points at it.");
        SweepOldVersions(installRoot, release.Version.ToString(), exePath, _log);

        // No handoff here: nothing in use was replaced, so the new build can simply be
        // started with the arguments this process was given.
        if (_options.Relaunch)
            Relaunch(stagedExe, Environment.GetCommandLineArgs()[1..], _log);

        return new UpdateResult(UpdateOutcome.Staged, release.Version);
    }

    /// <summary>
    /// Shorthand for <see cref="FetchAsync"/> followed by <see cref="ApplyAsync"/>:
    /// resolve the release to move to and, if there is one, download, validate, stage,
    /// and hand off. Reports <see cref="UpdateOutcome.UpToDate"/> when nothing applies.
    /// </summary>
    public async Task<UpdateResult> UpdateAsync(CancellationToken ct = default)
    {
        var release = await FetchAsync(ct).ConfigureAwait(false);
        if (release is null)
            return new UpdateResult(UpdateOutcome.UpToDate);
        return await ApplyAsync(release, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Name of the pointer file naming the active version, directly under the install
    /// root. A plain file rather than a symlink because replacing one file is the only
    /// atomic operation every platform agrees on: <c>rename()</c> on POSIX,
    /// <c>MoveFileEx(MOVEFILE_REPLACE_EXISTING)</c> on Windows.
    /// </summary>
    public const string CurrentFileName = "current.version";

    /// <summary>
    /// Name of the convenience symlink to the active version directory, maintained on
    /// POSIX only and on a best-effort basis, so a launcher can use a stable path (e.g.
    /// systemd's <c>ExecStart=/opt/app/current/app</c>). <see cref="CurrentFileName"/>
    /// remains the source of truth; the link is rebuilt from it, not the other way round.
    /// </summary>
    public const string CurrentLinkName = "current";

    private const string VersionsDirName = "versions";
    private const string StagingPrefix = ".staging-";

    /// <summary>
    /// The directory holding the active version of a versioned install, or <c>null</c>
    /// when the install root has no usable pointer. Call this at launch to resolve what
    /// to run; see <see cref="UpdaterOptions.InstallRoot"/> for the layout.
    /// </summary>
    public static string? ResolveCurrent(string installRoot)
    {
        try
        {
            var pointer = Path.Combine(installRoot, CurrentFileName);
            if (!File.Exists(pointer))
                return null;
            var version = File.ReadAllText(pointer).Trim();
            if (version.Length == 0)
                return null;
            var dir = Path.Combine(installRoot, VersionsDirName, version);
            return Directory.Exists(dir) ? dir : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Where <paramref name="exePath"/> sits relative to its version directory, or
    /// <c>null</c> when it is not inside the install at all. Both launch paths are
    /// accepted: straight out of <c>versions/&lt;v&gt;</c>, or through the
    /// <see cref="CurrentLinkName"/> symlink.
    /// </summary>
    private static string? RelativeExePath(string installRoot, string? exePath)
    {
        if (string.IsNullOrEmpty(exePath))
            return null;
        var full = Path.GetFullPath(exePath);

        var bases = new List<string> { Path.Combine(installRoot, CurrentLinkName) };
        var versionsDir = Path.Combine(installRoot, VersionsDirName);
        if (Directory.Exists(versionsDir))
            bases.AddRange(Directory.GetDirectories(versionsDir));

        foreach (var dir in bases)
        {
            var rel = Path.GetRelativePath(dir, full);
            if (
                !Path.IsPathRooted(rel)
                && rel != ".."
                && !rel.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            )
            {
                return rel;
            }
        }
        return null;
    }

    /// <summary>
    /// Point the install at <paramref name="version"/>. Writes a temporary file and moves
    /// it over the pointer, so a reader sees either the old version or the new one.
    /// </summary>
    private static bool SetCurrent(string installRoot, string version, TextWriter log)
    {
        var pointer = Path.Combine(installRoot, CurrentFileName);
        var tmp = pointer + ".tmp";
        try
        {
            File.WriteAllText(tmp, version);
            File.Move(tmp, pointer, overwrite: true);
        }
        catch (Exception e)
        {
            log.WriteLine($"Could not write {CurrentFileName}: {e.Message}");
            TryDelete(tmp);
            return false;
        }

        UpdateCurrentLink(installRoot, version, log);
        return true;
    }

    /// <summary>
    /// Rebuild the <see cref="CurrentLinkName"/> symlink. Best effort and POSIX-only:
    /// failures are logged and ignored, since the pointer file is what actually decides
    /// the active version. Not atomic (the old link is removed before the new one is
    /// created), which is why it is a convenience rather than the mechanism.
    /// </summary>
    private static void UpdateCurrentLink(string installRoot, string version, TextWriter log)
    {
        if (OperatingSystem.IsWindows())
            return;

        var link = Path.Combine(installRoot, CurrentLinkName);
        var target = Path.Combine(VersionsDirName, version);
        try
        {
            // Only ever remove a symlink; never follow one into a real directory.
            var info = new DirectoryInfo(link);
            if (info.Exists || info.LinkTarget is not null)
            {
                if (info.LinkTarget is null)
                {
                    log.WriteLine(
                        $"{CurrentLinkName} is a real directory, not a symlink; leaving it alone."
                    );
                    return;
                }
                info.Delete();
            }
            Directory.CreateSymbolicLink(link, target);
        }
        catch (Exception e)
        {
            log.WriteLine($"Could not update the {CurrentLinkName} symlink: {e.Message}");
        }
    }

    /// <summary>
    /// Delete version directories that are neither the newly active one nor the one the
    /// running process lives in, plus any leftover staging directories. Entirely best
    /// effort: on Windows a directory still in use simply refuses to delete, which is the
    /// wanted behaviour — it gets swept on a later run instead.
    /// </summary>
    private static void SweepOldVersions(
        string installRoot,
        string keepVersion,
        string? exePath,
        TextWriter log
    )
    {
        var versionsDir = Path.Combine(installRoot, VersionsDirName);
        if (!Directory.Exists(versionsDir))
            return;

        var running = RunningVersionDir(installRoot, exePath);
        var keep = Path.Combine(versionsDir, keepVersion);

        foreach (var dir in Directory.GetDirectories(versionsDir))
        {
            var name = Path.GetFileName(dir);
            if (!name.StartsWith(StagingPrefix, StringComparison.Ordinal))
            {
                if (PathsEqual(dir, keep) || (running is not null && PathsEqual(dir, running)))
                    continue;
            }
            if (!TryDelete(dir))
                log.WriteLine($"Left {name} in place (still in use).");
        }
    }

    /// <summary>
    /// The version directory <paramref name="exePath"/> lives in, if any. Passed in
    /// rather than read from the process so the sweep honours
    /// <see cref="UpdaterOptions.TargetPath"/> — the running build must survive the
    /// sweep even though the pointer no longer names it.
    /// </summary>
    private static string? RunningVersionDir(string installRoot, string? exePath)
    {
        var exe = exePath;
        if (string.IsNullOrEmpty(exe))
            return null;
        var versionsDir = Path.Combine(installRoot, VersionsDirName);
        if (!Directory.Exists(versionsDir))
            return null;

        var full = Path.GetFullPath(exe);
        foreach (var dir in Directory.GetDirectories(versionsDir))
        {
            var rel = Path.GetRelativePath(dir, full);
            if (!Path.IsPathRooted(rel) && !rel.StartsWith("..", StringComparison.Ordinal))
                return dir;
        }
        return null;
    }

    private static bool PathsEqual(string a, string b) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal
        );

    /// <summary>Delete a file or directory, reporting whether it is gone. Never throws.</summary>
    private static bool TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
            else if (File.Exists(path))
                File.Delete(path);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static Release? Newest(IReadOnlyList<Release> releases, Func<Release, bool>? filter)
    {
        Release? best = null;
        foreach (var release in releases)
        {
            if (filter is not null && !filter(release))
                continue;
            if (best is null || release.Version.ComparePrecedenceTo(best.Version) > 0)
                best = release;
        }
        return best;
    }

    private async Task<string?> DownloadAndValidateAsync(
        SourceAsset asset,
        string target,
        CancellationToken ct
    )
    {
        var tempDir = NewStagingDir();
        // Name the staged file after the target so the swapped-in binary keeps its name.
        var staged = Path.Combine(tempDir, Path.GetFileName(target));

        if (!await DownloadToFileAsync(asset, staged, ct).ConfigureAwait(false))
            return null;
        if (!await VerifyChecksumAsync(staged, asset, ct).ConfigureAwait(false))
            return null;

        if (!OperatingSystem.IsWindows())
            Utilities.MakeExecutable(staged);

        if (!await ValidateAsync(staged, ct).ConfigureAwait(false))
            return null;

        return staged;
    }

    /// <summary>
    /// Download the release archive, verify its checksum, and extract it into
    /// <paramref name="tempDir"/>. Returns the staged build root — the single top-level
    /// directory inside the archive, or the extraction directory itself when the archive
    /// has no single wrapping directory — or <c>null</c> on failure.
    /// </summary>
    private async Task<string?> DownloadAndExtractAsync(
        SourceAsset asset,
        string tempDir,
        CancellationToken ct
    )
    {
        Directory.CreateDirectory(tempDir);
        // Keep the asset's own file name so extraction can dispatch on its extension.
        var fileName = Path.GetFileName(asset.Name);
        if (string.IsNullOrEmpty(fileName))
            fileName = "download.zip";
        var archive = Path.Combine(tempDir, fileName);

        if (!await DownloadToFileAsync(asset, archive, ct).ConfigureAwait(false))
            return null;
        if (!await VerifyChecksumAsync(archive, asset, ct).ConfigureAwait(false))
            return null;

        var extractDir = Path.Combine(tempDir, "extracted");
        try
        {
            ExtractArchive(archive, extractDir);
        }
        catch (Exception e)
        {
            _log.WriteLine($"Extraction failed: {e.Message}");
            return null;
        }

        var dirs = Directory.GetDirectories(extractDir);
        var files = Directory.GetFiles(extractDir);
        return dirs.Length == 1 && files.Length == 0 ? dirs[0] : extractDir;
    }

    /// <summary>
    /// Extract a release archive to <paramref name="extractDir"/>, dispatching on the
    /// archive's file extension: <c>.tar.gz</c>/<c>.tgz</c> via gzip + tar, everything
    /// else as a <c>.zip</c>. Both restore Unix file permissions recorded in the archive
    /// (and tar restores symlinks), so executable bits inside a bundle survive the
    /// round-trip.
    /// </summary>
    internal static void ExtractArchive(string archivePath, string extractDir)
    {
        if (IsTarball(archivePath))
        {
            Directory.CreateDirectory(extractDir);
            using var file = File.OpenRead(archivePath);
            using var gzip = new GZipStream(file, CompressionMode.Decompress);
            TarFile.ExtractToDirectory(gzip, extractDir, overwriteFiles: true);
        }
        else
        {
            ZipFile.ExtractToDirectory(archivePath, extractDir);
        }
    }

    private static bool IsTarball(string name) =>
        name.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase);

    private static string NewStagingDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "selfupdater-" + Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        return dir;
    }

    private async Task<bool> DownloadToFileAsync(
        SourceAsset asset,
        string path,
        CancellationToken ct
    )
    {
        _log.WriteLine($"Downloading {asset.Location}...");
        try
        {
            await using var src = await OpenAssetAsync(asset, ct).ConfigureAwait(false);
            await using var file = new FileStream(
                path,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None
            );
            await src.CopyToAsync(file, ct).ConfigureAwait(false);
            return true;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _log.WriteLine($"Download failed: {e.Message}");
            return false;
        }
    }

    private async Task<bool> VerifyChecksumAsync(
        string path,
        SourceAsset asset,
        CancellationToken ct
    )
    {
        if (string.IsNullOrEmpty(asset.Sha256))
            return true;

        var actual = await Utilities.ComputeSha256Async(path, ct).ConfigureAwait(false);
        if (!actual.Equals(asset.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            _log.WriteLine($"Checksum mismatch: expected {asset.Sha256}, got {actual}.");
            return false;
        }
        _log.WriteLine("Checksum OK.");
        return true;
    }

    private async Task<bool> ValidateAsync(string path, CancellationToken ct)
    {
        // Validation is opt-in: with no args configured we do not execute the
        // freshly downloaded binary, since there is no universally safe probe.
        if (_options.ValidateArgs is not { Count: > 0 } validateArgs)
            return true;

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = path,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var a in validateArgs)
                psi.ArgumentList.Add(a);

            using var proc = Process.Start(psi);
            if (proc is null)
            {
                _log.WriteLine("Could not start the downloaded binary for validation.");
                return false;
            }
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);
            if (proc.ExitCode != 0)
            {
                _log.WriteLine($"Downloaded binary failed validation (exit {proc.ExitCode}).");
                return false;
            }
            return true;
        }
        catch (Exception e)
        {
            _log.WriteLine($"Validation error: {e.Message}");
            return false;
        }
    }

    private bool LaunchHandoff(string stagedPath, string target)
    {
        _log.WriteLine($"Staged update ready; handing off to replace {target}.");
        var psi = new ProcessStartInfo
        {
            FileName = stagedPath,
            ArgumentList =
            {
                HandoffVerb,
                DestOption,
                target,
                PidOption,
                Environment.ProcessId.ToString(),
            },
        };
        if (_options.Relaunch)
            psi.ArgumentList.Add(RelaunchOption);

        return Process.Start(psi) is not null;
    }

    /// <summary>
    /// The handoff (new-process) side of the single-file swap. Runs from the freshly
    /// staged binary: waits for the previous process to exit, copies itself over
    /// <paramref name="destPath"/>, and optionally relaunches. Wire this up in your entry
    /// point under <see cref="HandoffVerb"/>.
    /// <para>
    /// Versioned installs (<see cref="UpdaterOptions.InstallRoot"/>) need no handoff at
    /// all: they never replace a file that is in use, so there is nothing that has to
    /// wait for the old process to exit.
    /// </para>
    /// </summary>
    public static int ApplySwap(
        string destPath,
        int oldPid,
        IReadOnlyList<string>? relaunchArgs = null,
        TextWriter? log = null
    )
    {
        log ??= Console.Out;
        WaitForExit(oldPid, log);
        return ApplyFileSwap(destPath, relaunchArgs, log);
    }

    private static void WaitForExit(int oldPid, TextWriter log)
    {
        if (oldPid <= 0)
            return;
        try
        {
            using var old = Process.GetProcessById(oldPid);
            log.WriteLine($"Waiting for previous process (pid {oldPid}) to exit...");
            if (!old.WaitForExit(30_000))
                log.WriteLine("Previous process did not exit in time; attempting swap anyway.");
        }
        catch (ArgumentException)
        {
            // Process already gone.
        }
    }

    private static int ApplyFileSwap(
        string destPath,
        IReadOnlyList<string>? relaunchArgs,
        TextWriter log
    )
    {
        var src = Utilities.ProcessPath;
        if (string.IsNullOrEmpty(src))
        {
            log.WriteLine("Cannot apply update: unknown source path.");
            return 1;
        }

        var backup = destPath + ".bak";
        try
        {
            if (File.Exists(destPath))
                File.Move(destPath, backup, overwrite: true);

            // Copy (rather than move) the still-running staged binary so the swap
            // works across volumes and the running image stays valid.
            File.Copy(src, destPath, overwrite: true);
            File.SetLastWriteTimeUtc(destPath, DateTime.UtcNow);
            if (!OperatingSystem.IsWindows())
                Utilities.MakeExecutable(destPath);

            if (File.Exists(backup))
            {
                try
                {
                    File.Delete(backup);
                }
                catch
                { /* a locked .bak on Windows is harmless; leave it for next run */
                }
            }
        }
        catch (Exception e)
        {
            log.WriteLine($"Swap failed: {e.Message}");
            if (File.Exists(backup) && !File.Exists(destPath))
            {
                try
                {
                    File.Move(backup, destPath);
                }
                catch
                { /* best effort */
                }
            }
            return 1;
        }

        log.WriteLine($"Updated in place: {destPath}");
        Relaunch(destPath, relaunchArgs, log);
        return 0;
    }

    private static void Relaunch(string exe, IReadOnlyList<string>? relaunchArgs, TextWriter log)
    {
        if (relaunchArgs is null)
            return;
        log.WriteLine("Relaunching...");
        var psi = new ProcessStartInfo { FileName = exe };
        foreach (var a in relaunchArgs)
            psi.ArgumentList.Add(a);
        Process.Start(psi);
    }
}
