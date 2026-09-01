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

## Three ready-to-use updaters

Pick the one that matches where your releases live, hand it an `UpdaterOptions`, and
call `UpdateAsync`:

| Updater | Use it for |
|---|---|
| `DirectoryUpdater` | A local folder or network share — LAN/offline/air-gapped rollouts, and tests. Reads the directory's files; the ones matching `{appName}-{version}-{rid}` (or your parser) become releases. Optional `{binary}.sha256` sidecars provide integrity. |
| `GitHubUpdater` | GitHub Releases. Reads each published asset; the ones matching `{appName}-{version}-{rid}` (or your parser) become releases — the rest are ignored. Public repos need nothing; **private** repos take an `authToken` delegate and download through the authenticated asset API. |
| `ForgejoUpdater` | A [Forgejo](https://forgejo.org) (or Gitea) instance's Releases, including a self-hosted one. Same naming rules as above, read through the instance's `/api/v1` REST API. Forgejo publishes no asset digest, so integrity comes from `{asset}.sha256` sidecar assets. Takes the same `authToken` delegate for private repos and login-required instances. |

All three share the same engine via a common `Updater` base: a concrete updater only
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

For **single-file** updates, `UpdateAsync` downloads + validates the new binary,
then launches it with a hidden command so the **new** process performs the swap
once the old one exits (a running executable cannot overwrite itself, especially
on Windows). Wire that command up once:

```csharp
// e.g. with System.CommandLine — names come from Updater constants
// Updater.HandoffVerb ("apply-update"), Updater.DestOption ("--dest"),
// Updater.PidOption ("--pid"), Updater.RelaunchOption ("--relaunch")
if (args is [Updater.HandoffVerb, ..])
    return Updater.ApplySwap(destPath, oldPid, relaunchArgs: null);
```

Versioned installs (below) need no handoff: they never replace anything that is
in use, so nothing has to wait for the old process to exit.

### Versioned (multi-file) installs

Some apps are not a single file — a binary that ships sidecar native assets next
to it, say. Set `InstallRoot` and the engine treats the release asset as a `.zip`
or `.tar.gz`/`.tgz` archive and installs each version into its own directory,
moving a pointer to select the active one:

```
<InstallRoot>/
  current              pointer: a symlink to versions/<active> on POSIX,
                       or a small file naming it on Windows (current.version)
  versions/1.2.3/      the running build; never touched by an update
  versions/1.2.4/      freshly unpacked
```

```csharp
var updater = new GitHubUpdater("you", "myapp", new UpdaterOptions
{
    AppName = "myapp",
    CurrentVersion = current,
    InstallRoot = installRoot,
});
```

**Nothing in use is ever renamed, deleted, or overwritten.** An update unpacks
into a `versions/` directory that nothing points at, validates it, and only then
replaces the pointer. An interrupted update therefore leaves a junk directory and
a completely working app, with no backup tree, no journal, and no reconciliation
pass to get wrong. Rolling back is pointing at a version that is still on disk.

There is exactly **one** pointer, and replacing it is the only operation in the
whole update that has to be atomic:

| | Pointer | Replaced with |
|---|---|---|
| POSIX | `current` → `versions/<active>` symlink | `rename(2)` |
| Windows | `current.version`, a file naming the version | `MoveFileEx(MOVEFILE_REPLACE_EXISTING)` |

(The BCL cannot swap a directory symlink — `File.Move` rejects one because
`File.Exists` is false for it, and `Directory.Move` refuses to overwrite — so the
POSIX path calls `rename(2)` directly rather than leaving a window where the
pointer does not exist.)

#### Launching the active version

On **POSIX the pointer is a symlink, so nothing extra is needed**: point whatever
starts your app at the stable path and it follows updates by itself.

```ini
ExecStart=/opt/myapp/current/myapp
```

**Windows has no unprivileged directory symlink**, so a shortcut or service
cannot point at a stable path. Ship a launcher: a tiny executable that lives at
the install root, outside the version directories, and is what shortcuts and
service definitions point at.

```csharp
// launcher/Program.cs — the whole thing
return Updater.RunLauncher(AppContext.BaseDirectory, "myapp.exe", args);
```

`RunLauncher` resolves the pointer, starts that executable inside the active
version, and returns its exit code. Child stdio is inherited, so console output
and Ctrl-C behave as if the app had been started directly. It does not supervise
beyond waiting — kill the launcher and the child keeps running; setting up a
Windows job object to change that is left to the caller.

Because the launcher lives outside the version directories, a versioned install
does not replace it. When a release ships a newer one, stage it and it is
promoted on the next run:

```csharp
Updater.StageLauncherReplacement(launcherPath, newLauncherPath);
```

The swap is deferred because a running executable cannot be overwritten — but it
*can* be renamed, even on Windows, so promotion renames the old launcher aside
and moves the replacement into place.

`Updater.ResolveCurrent(installRoot)` returns the active version's directory if
you need to resolve it yourself.

The running executable must live inside a version directory (directly, or via the
pointer); its location relative to that directory is how the staged build's
executable is found. Old versions are swept on a best-effort basis after each
successful install — the running one and the newly active one are always kept,
and on Windows a directory still in use simply refuses to delete and is swept on
a later run.

The release asset is named the same way — `{appName}-{version}-{rid}.<ext>` — the
default convention strips a known archive extension (`.zip`, `.tgz`, `.tar.gz`)
before matching, and extraction dispatches on that extension (`.tar.gz`/`.tgz` are
extracted as gzipped tar, everything else as zip). Executable bits inside the
archive are preserved, so the installed tree stays runnable.

> **Not for macOS `.app` bundles.** A bundle is an identity, not just a directory:
> Launch Services, the Dock, `open -a` and TCC grants all key on its path, and it
> must be signed and notarized as a unit, so versioned directories behind a stub
> fight the platform. macOS also has a genuinely atomic directory exchange
> (`renamex_np(..., RENAME_SWAP)`) and a mature framework built on it. Use
> [Sparkle](https://sparkle-project.org) for Mac app bundles.

### Forgejo / Gitea

`ForgejoUpdater` takes the instance URL on top of owner/repo; everything else is the
same as `GitHubUpdater`:

```csharp
var updater = new ForgejoUpdater(
    instance: new Uri("https://forge.example.internal"),
    owner: "you", repo: "myapp", options,
    authToken: ct => Task.FromResult<string?>(myToken)); // omit for public repos
```

Two differences from GitHub are worth knowing:

- **Checksums are sidecars.** GitHub reports a `digest` per asset; Forgejo reports
  none, so upload `{assetName}.sha256` next to each build (the same convention
  `DirectoryUpdater` uses — a bare hash or `sha256sum` output both parse). Without
  one, the download is not checksum-verified. Sidecars are fetched only for assets
  matching the configured rid, and a custom `Parser` disables the lookup since the
  naming it implies is unknown here.
- **Auth is scoped to the instance.** The token goes out as Forgejo's
  `Authorization: token …`, and only on requests to the instance host — a Forgejo
  release asset may be an arbitrary external URL, which never sees your credentials.

Do not use the `releases.rss` feed for this: it carries no asset list, download URLs,
sizes, or prerelease flag. The REST API is the supported path.

### Updating several binaries from one repo

`AppName` is what selects a build, so two executables published to the same
repo — a client and a server, say, that may not even live on the same machine —
update independently. Give each its own updater:

```csharp
var client = new ForgejoUpdater(instance, "you", "myapp", options with { AppName = "myapp" });
var server = new ForgejoUpdater(instance, "you", "myapp", options with { AppName = "myapp-server" });
```

Ship `myapp-1.2.3-linux-arm64.tar.gz` and `myapp-server-1.2.3-linux-arm64.tar.gz` in
the same release and each side picks up only its own: the `myapp` updater rejects the
server asset because `server-1.2.3` is not a version, and the `myapp-server` updater
rejects the client asset on the prefix. Their versions are then free to diverge, so if
the two speak a protocol to each other, that compatibility is yours to manage.

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
