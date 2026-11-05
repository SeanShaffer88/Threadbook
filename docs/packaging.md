# Packaging and startup storage

Beta.2 provides a per-user installer and a portable ZIP. Both require the **.NET 10 Desktop Runtime for Windows x64**, installed manually from Microsoft. Neither package includes, downloads or runs a runtime installer. No automatic updater is included.

## Reproduce packages

Use PowerShell 7, .NET SDK 10.0.403 and a trusted installed Inno Setup compiler. The script accepts Inno 6/7 command-line `/D` and `/O` options. The local qualification used Inno 7.1.1 after the sandbox denied access to the installed Inno 6 executable. Compiler provenance, path and SHA-256 are private build evidence; no compiler is downloaded or installed by these scripts or CI. Official compiler/directive help: <https://jrsoftware.org/ishelp/>.

```powershell
pwsh -NoProfile -File tools/Build-Packages.ps1 -Version 0.2.0-beta.2 -IsccPath '<installed-ISCC.exe>' -OutputRoot '<new-absolute-directory-outside-repository>'
pwsh -NoProfile -File tools/Test-Packages.ps1 -PackageRoot '<package-directory>' -FixtureRoot '<new-absolute-fixture-directory>' -RunSmoke
```

`-SkipInstaller` produces the complete portable package without a compiler. Windows CI runs this path and the headless startup suite; CI does not qualify the installer runtime. `-BuildRunnerPath` optionally accepts a local DevTools Invoke-DevBuild.ps1. Otherwise publish/compiler output and native exit codes are retained directly in the package directory. A failed native command exits with that command's code; no existing output is replaced. Output paths must be new, absolute, outside the repository, and free of filesystem links. Partial failed output is retained for diagnosis; retry with a new destination.

Publishing uses Release, win-x64, framework-dependent mode, `DebugType=None`, `DebugSymbols=false` and no shared compiler. The six application files must exactly match `packaging/payload-manifest.json`. Installer file entries are explicit; the portable ZIP additionally contains empty `portable.flag`, README and MIT license. No data, PDBs, test fixtures or notebooks are distributed. `app-manifest.json` records payload hashes and `SHA256SUMS.txt` records ZIP/setup hashes. Hashes establish identity, not signing or trust. The installer is unsigned.

## Storage and upgrade contract

An explicit `--data-dir` wins. Otherwise `portable.flag` beside the executable selects adjacent `Data`, resolved with `AppContext.BaseDirectory` regardless of current directory. Without a marker, the existing `%LOCALAPPDATA%\Threadbook` default applies. Selection does not write or migrate data. Portable marker/Data ancestor links are rejected. Any storage write failure is visible and never selects another library. Automated modes still require an explicit synthetic `--data-dir`.

The installer uses a stable AppId, lowest privileges, current-user shortcuts, selectable destination and ordinary uninstaller. It has no Run section, startup registration, runtime installer or notebook deletion rules. The welcome page displays the manual runtime prerequisite. Portable-marked, notebook-bearing, and other targets with unexpected entries are rejected both at directory selection and immediately before installation. Close Threadbook manually before upgrading. Back up your library independently; copy portable upgrades into a new application folder and transfer Data explicitly while closed. Keep installed app files separate from notebooks.

## Qualification limits and lifecycle plan

Local compile/static inventory and synthetic portable WPF smoke are distinct from installed lifecycle qualification. Headless startup tests exercise real portable storage, foreign CWD, explicit priority, exclusive-lock write failure, and OS-enforced read-only lock-file denial. A directory ACL-based read-only portable test remains unperformed.

Before claiming installed lifecycle qualification, use an isolated Windows guest to install, repeat same-AppId setup as a repair/upgrade, uninstall and inspect shortcuts/startup/process state. Create a synthetic notebook outside the app directory and preserve its hash across all phases. Test rejecting marked portable and notebook-bearing destinations. Confirm missing-runtime messaging on a clean machine. The generated DevTools lab must be reviewed before launch; generation is not execution. Upgrade from an actual earlier installer is pending because beta.1 had no installer. No host installer or VM is launched by the build scripts.

The generated lifecycle plan intentionally has install and same-version repair phases only. DevTools requires every phase executable to be staged before generation; the generated uninstaller does not exist until guest installation. Uninstall, shortcut cleanup and notebook preservation therefore need manual guest checks, and are not covered by this partial plan.
