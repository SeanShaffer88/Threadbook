# Verification

## Reproduce the headless checks

Use Windows, PowerShell 7, .NET SDK 10.0.403, and the .NET 10 Windows desktop targeting support. Local recorded execution used Microsoft.WindowsDesktop.App 10.0.14. Run the following from the repository root. It creates unique disposable state outside the checkout, uses absolute project paths, checks every native exit code, and leaves fixtures available for inspection.

```powershell
$ErrorActionPreference = 'Stop'
$repo = (Get-Location).Path
$originalDirectory = $repo
$state = Join-Path ([IO.Path]::GetTempPath()) ('threadbook-check-' + [guid]::NewGuid().ToString('N'))
$names = @('APPDATA','LOCALAPPDATA','DOTNET_CLI_HOME','NUGET_PACKAGES','TEMP','TMP')
$previous = @{}
foreach ($name in $names) {
  $previous[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
  $path = Join-Path $state $name
  New-Item -ItemType Directory -Path $path -Force | Out-Null
  [Environment]::SetEnvironmentVariable($name, $path, 'Process')
}
$previousTelemetry = $env:DOTNET_CLI_TELEMETRY_OPTOUT
$previousResolver = $env:MSBuildEnableWorkloadResolver
try {
  $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
  $env:MSBuildEnableWorkloadResolver = 'false'
  Set-Location $state
  dotnet build (Join-Path $repo 'desktop/Threadbook.Desktop.csproj') -c Release --warnaserror ('--artifacts-path=' + (Join-Path $state 'artifacts')) -p:UseSharedCompilation=false
  if ($LASTEXITCODE -ne 0) { throw "Desktop build failed: $LASTEXITCODE" }
  $projects = @(
    'tests/Threadbook.Tests.csproj',
    'writing-tests/Threadbook.WritingTests.csproj',
    'layout-tests/Threadbook.LayoutTests.csproj',
    'library-domain-tests/Threadbook.LibraryDomainTests.csproj',
    'library-storage-tests/Threadbook.LibraryStorageTests.csproj',
    'private-files-tests/Threadbook.PrivateFilesTests.csproj',
    'archive-storage-tests/Threadbook.ArchiveStorage.Tests.csproj',
    'pane-sizing-tests/Threadbook.PaneSizingTests.csproj',
    'wiki-tests/Threadbook.WikiTests.csproj'
  )
  foreach ($project in $projects) {
    $arguments = @('run','--project',(Join-Path $repo $project),'-c','Release','--property:TreatWarningsAsErrors=true','--property:UseSharedCompilation=false',('--artifacts-path=' + (Join-Path $state 'artifacts')))
    if ($project -like 'pane-sizing-tests/*' -or $project -like 'archive-storage-tests/*') {
      $arguments += @('--',(Join-Path $state ('work-fixture-' + [guid]::NewGuid().ToString('N'))))
    }
    dotnet @arguments
    if ($LASTEXITCODE -ne 0) { throw "Suite failed: $project ($LASTEXITCODE)" }
  }
} finally {
  Set-Location $originalDirectory
  foreach ($name in $names) { [Environment]::SetEnvironmentVariable($name, $previous[$name], 'Process') }
  $env:DOTNET_CLI_TELEMETRY_OPTOUT = $previousTelemetry
  $env:MSBuildEnableWorkloadResolver = $previousResolver
  Write-Host "Inspect retained fixtures at $state"
}
```

NuGet.Config clears package sources. These projects use framework references, not external PackageReferences. Missing SDK/targeting packs require an appropriate local installation; an offline restore does not prove SDK readiness.

## Recorded local development evidence

The following results describe the integrated five-feature development phase, before final beta packaging. Each listed execution returned exit 0. Counts describe that recorded phase and may grow with later changes; they are not assertions about an unrun hosted workflow or a subsequently rebuilt release.

| Gate | Recorded result |
| --- | --- |
| Core/storage console suite | 9 suites |
| Writing console suite | 40 tests |
| Layout/preferences logic suite | 18 tests |
| Archive storage suite | 421 assertions |
| Inline editor WPF suite | 189 assertions |
| Protected-content WPF suite | 61 assertions |
| Command palette WPF suite | 33 assertions |
| Pane WPF suite | 55 assertions across six layouts |
| Chrome WPF suite | 35 PASS markers |
| Wiki Core suite | 19 tests |

The complete isolated command block above was executed locally with exit 0: Desktop built with zero warnings/errors; the nine console suites reported 9 core/storage suites, 40 writing tests, 18 layout/preferences tests, 63 library-domain assertions, 74 library-storage assertions, 212 private-file checks, 421 archive assertions, 40 pane-preference assertions, and 19 Wiki tests. These are the current headless results. The WPF counts in the table are prior integrated-phase records. WPF tests operate on synthetic fixtures and exercise actual controls; they do not establish physical keyboard or mouse behavior. Additional preview-callback regression and final integrated checks are recorded by their own run output rather than being included in the historical counts above.

## CI and release evidence

[CI](.github/workflows/ci.yml) builds Desktop and runs the nine headless console suites on Windows with SDK 10.0.403. It isolates application data, CLI state, package cache, and temporary fixtures outside the checkout. Official checkout/setup-dotnet actions use immutable commit pins, checkout credentials are not persisted, and workflow permissions are limited to reading contents. No GUI suite is claimed to run in this workflow.

See [GitHub Actions](https://github.com/SeanShaffer88/Threadbook/actions) for actual hosted-runner results for a commit. See [Releases](https://github.com/SeanShaffer88/Threadbook/releases) for each published artifact's source revision, archive inventory, SHA-256 checksums, and recorded runtime checks. A local source check does not establish hosted CI success or final ZIP correctness.

The README hero is an actual RenderTargetBitmap capture of the WPF app using a newly created synthetic library. It demonstrates the source UI, not the identity or verification of a downloadable beta binary. Personal notes and local test logs are excluded from public assets.

## Verification limits

Unperformed checks include physical keyboard/mouse interaction, native Windows Snap hover, real multi-monitor/DPI transitions, screen-reader and accessibility certification, prolonged endurance, and filling native arrays to the complete 5,000-entry/512-MiB archive boundary. Reduced fixtures and bounded tests are not full-capacity measurements. No installer lifecycle, signing, automatic update, or performance certification is claimed.

Encryption tests cannot prove complete managed-heap erasure. File hashes establish identity rather than trust. Runtime and source checks apply to the tested configuration; use the release-specific evidence when evaluating a binary.
