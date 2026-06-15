# SelfUpdater

A small, pluggable self-update engine for single-file / Native AOT .NET apps.

It does what [dnvm](https://github.com/dn-vm/dnvm) does for itself, generalized:
check the running version against a source, download the build for your platform,
verify (SHA-256, when the source publishes a hash) and optionally
validate (smoke-test) it, then replace the running executable **in place** via a
two-process handoff so an app can update itself — including on Windows, where a
running image can't overwrite itself. Single-file binaries and multi-file bundles
(e.g. a macOS `.app`) are both supported — see
[Directory (multi-file) updates](#directory-multi-file-updates).

## Install

```
dotnet add package SelfUpdater
```

Targets `net10.0`, trim/AOT-compatible, serializes with [Serde.NET](https://github.com/serde-dotnet/serde).

## Two ready-to-use updaters

Pick the one that matches where your releases live, hand it an `UpdaterOptions`, and
call `UpdateAsync`:

| Updater | Use it for |
|---|---|
| `DirectoryUpdater` | A local folder or network share — LAN/offline/air-gapped rollouts, and tests. Reads the directory's files; the ones matching `{appName}-{version}-{rid}` (or your parser) become releases. Optional `{binary}.sha256` sidecars provide integrity. |
| `GitHubUpdater` | GitHub Releases. Reads each published asset; the ones matching `{appName}-{version}-{rid}` (or your parser) become releases — the rest are ignored. Public repos need nothing; **private** repos take an `authToken` delegate and download through the authenticated asset API. |

Both share the same engine via a common `Updater` base: a concrete updater only
supplies how to *list* a source's raw artifacts and how to *open* one's bytes — it
never parses versions, compares them, decides what counts as "new", or picks which
asset fits the running platform. All of that policy lives in `UpdaterOptions`, which
knows your current version and target platform (its runtime identifier).

## Usage

The engine is **policy-free**: you tell it your current version (it never fetches
it for you) via `UpdaterOptions`, and it decides nothing about what is "new" unless
you ask it to. Naming and platform selection live in `UpdaterOptions`. Build the
options, create an updater, and call `UpdateAsync`:

```csharp
using Semver;
using SelfUpdater;

var updater = new DirectoryUpdater("/path/to/releases", new UpdaterOptions
{
    AppName = "myapp",
    // You own the current version; the engine never fetches it for you. Versions
    // are Semver.SemVersion (the Semver NuGet package).
    CurrentVersion = SemVersion.Parse(MyApp.Version, SemVersionStyles.Any),
    // Rid defaults to RuntimeInformation.RuntimeIdentifier — the running platform.
    // It selects assets named `{AppName}-{version}-{Rid}` and is what lets the
    // engine split that name unambiguously (both the version and the rid may
    // contain dashes). Set Parser for a naming scheme other than the default.
    // TargetPath defaults to the running executable.
    // ValidateArgs is opt-in: leave unset to skip executing the download as a
    // smoke test, or set e.g. ["--version"] if your binary exits 0 for those.
});

// Newest-wins against CurrentVersion, for the selected platform.
var result = await updater.UpdateAsync();
if (result.Outcome == UpdateOutcome.Staged)
    return 0; // a newer build was handed off; this process should now exit
```

For GitHub Releases, swap in `GitHubUpdater` — the options are identical:

```csharp
var updater = new GitHubUpdater("you", "myapp", options);
```

Set `ReleaseFilter` to ignore prereleases. To peek without applying, call
`FetchAsync()` — it does the network round-trip and returns the exact `Release`
you'd move to (or `null` when you're already current or there's no build for your
platform):

```csharp
var updater = new GitHubUpdater("you", "myapp", new UpdaterOptions
{
    AppName = "myapp",
    CurrentVersion = current,
    ReleaseFilter = r => !r.IsPrerelease,
});

var release = await updater.FetchAsync();
if (release is not null)
{
    Console.WriteLine($"New version {release.Version} available.");
    await updater.ApplyAsync(release); // download, verify, stage, hand off
}
```

The surface is three methods: `FetchAsync()` resolves the release to move to,
`ApplyAsync(release)` downloads and stages it, and `UpdateAsync()` is shorthand for
fetch-then-apply (so it never lists the source twice).

### The handoff command

`UpdateAsync` downloads + validates the new binary, then launches it with a
hidden command so the **new** process performs the swap once the old one exits.
Wire that command up once:

```csharp
// e.g. with System.CommandLine — names come from Updater constants
// Updater.HandoffVerb ("apply-update"), Updater.DestOption ("--dest"),
// Updater.PidOption ("--pid"), Updater.RelaunchOption ("--relaunch"),
// Updater.SourceDirOption ("--source-dir", directory updates only)
if (args is [Updater.HandoffVerb, ..])
{
    // sourceDir is null for single-file updates; pass it through for directory ones.
    return Updater.ApplySwap(destPath, oldPid, relaunchArgs: null, sourceDir: sourceDir);
}
```

### Directory (multi-file) updates

Some apps are not a single file — a macOS `.app` bundle, or a binary that ships
sidecar native assets next to it. Set `TargetDirectory` and the engine treats the
release asset as a `.zip` or `.tar.gz`/`.tgz` containing one top-level directory, and
replaces the whole tree in place instead of one file:

```csharp
var updater = new GitHubUpdater("you", "myapp", new UpdaterOptions
{
    AppName = "myapp",
    CurrentVersion = current,
    // The directory to replace wholesale. The running executable must live inside
    // it (e.g. MyApp.app/Contents/MacOS/myapp); its location relative to the root is
    // reused to launch the staged build and to relaunch after the swap.
    TargetDirectory = bundleRoot,
});
```

The release asset is named the same way — `{appName}-{version}-{rid}.<ext>` — the
default convention strips a known archive extension (`.zip`, `.tgz`, `.tar.gz`)
before matching, and extraction dispatches on that extension (`.tar.gz`/`.tgz` are
extracted as gzipped tar, everything else as zip). Executable bits inside the archive
are preserved on extraction, so the swapped-in tree stays runnable. The handoff is
identical; just forward the `--source-dir` value (above) to `ApplySwap`.

### Private GitHub repos

The library is auth-mechanism agnostic — `authToken` is a
`Func<CancellationToken, Task<string?>>` awaited per request, so a consumer can
fetch/cache/refresh short-lived or rotating tokens however it likes:

```csharp
var gh = new GitHubUpdater(
    owner: "you", repo: "private-app", options,
    authToken: ct => TokenCache.GetCurrentInstallationTokenAsync(ct)); // your concern
```

Use a fine-grained PAT (`Contents: read-only`), a GitHub App installation token
vended from a small endpoint, or an OAuth device-flow token — the library doesn't
care which.

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md) for build/test steps and the CSharpier
formatting requirement.

## License

MIT
