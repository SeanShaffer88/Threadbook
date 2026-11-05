param([Parameter(Mandatory)][string]$PackageRoot, [Parameter(Mandatory)][string]$FixtureRoot, [switch]$RunSmoke)
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/PackagePolicy.ps1"
$repo = Split-Path $PSScriptRoot
Assert-NoPackageLinks $PackageRoot
Assert-NewPackageDirectory $FixtureRoot $repo
Assert-AppPayload (Join-Path $PackageRoot 'app')
$manifest = Get-Content (Join-Path $PackageRoot 'app-manifest.json') -Raw | ConvertFrom-Json
if (@($manifest.files).Count -ne $script:AppFiles.Count -or @($manifest.files.path | Select-Object -Unique).Count -ne $script:AppFiles.Count) { throw 'Unexpected manifest inventory.' }
foreach ($file in $manifest.files) {
    if ($file.path -cnotin $script:AppFiles -or $file.sha256 -notmatch '^[0-9A-Fa-f]{64}$') { throw 'Invalid manifest entry.' }
    if ((Get-FileHash -LiteralPath (Join-Path "$PackageRoot/app" $file.path) -Algorithm SHA256).Hash -cne $file.sha256) { throw 'Payload hash mismatch.' }
}
New-Item -ItemType Directory -Path $FixtureRoot | Out-Null
$extracted = Join-Path $FixtureRoot 'extracted'
$zip = Join-Path $PackageRoot 'Threadbook-v0.2.0-beta.2-win-x64-portable.zip'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$expected = @($script:AppFiles) + @('portable.flag','README-portable.txt','LICENSE')
$archive = [IO.Compression.ZipFile]::OpenRead($zip)
try {
    $entries = @($archive.Entries)
    if ($entries.Count -ne $expected.Count -or @($entries.FullName | Select-Object -Unique).Count -ne $expected.Count) { throw 'Unexpected ZIP inventory.' }
    if (($entries | Measure-Object -Property Length -Sum).Sum -gt 268435456) { throw 'ZIP exceeds package size bound.' }
    foreach ($entry in $entries) { if ($entry.FullName -cnotin $expected) { throw 'Unexpected ZIP path.' } }
} finally { $archive.Dispose() }
Expand-Archive -LiteralPath $zip -DestinationPath $extracted
foreach ($name in $script:AppFiles) {
    if ((Get-FileHash -LiteralPath (Join-Path $extracted $name)).Hash -ne (Get-FileHash -LiteralPath (Join-Path "$PackageRoot/app" $name)).Hash) { throw 'ZIP payload differs from app.' }
}
if ((Get-Item -LiteralPath "$extracted/portable.flag").Length -ne 0) { throw 'Portable marker must be empty.' }
Assert-ArtifactChecksums $PackageRoot
# Actual isolated files exercise missing, duplicate, empty, stale and corrupt inventories.
$checksumFixture = Join-Path $FixtureRoot 'checksum-negative'
New-Item -ItemType Directory -Path $checksumFixture | Out-Null
$portableName = 'Threadbook-v0.2.0-beta.2-win-x64-portable.zip'
$setupName = 'Threadbook-v0.2.0-beta.2-win-x64-setup.exe'
Set-Content -LiteralPath (Join-Path $checksumFixture $portableName) -Value 'synthetic portable'
Set-Content -LiteralPath (Join-Path $checksumFixture $setupName) -Value 'synthetic setup'
$portableLine = "$((Get-FileHash (Join-Path $checksumFixture $portableName)).Hash.ToLowerInvariant())  $portableName"
$setupLine = "$((Get-FileHash (Join-Path $checksumFixture $setupName)).Hash.ToLowerInvariant())  $setupName"
$cases = @{
    empty = @(); duplicate = @($portableLine,$portableLine,$setupLine)
    missingPortable = @($setupLine); missingSetup = @($portableLine)
    setupMismatch = @($portableLine, ('0' * 64 + '  ' + $setupName))
}
foreach ($case in $cases.GetEnumerator()) {
    [IO.File]::WriteAllLines((Join-Path $checksumFixture 'SHA256SUMS.txt'), [string[]]$case.Value)
    $rejected = $false
    try { Assert-ArtifactChecksums $checksumFixture } catch { $rejected = $true }
    if (-not $rejected) { throw "Checksum negative fixture accepted: $($case.Key)" }
    "PASS: checksum rejects $($case.Key)" | Write-Output
}
[IO.File]::WriteAllLines((Join-Path $checksumFixture 'SHA256SUMS.txt'), [string[]]@($portableLine,$setupLine))
Assert-ArtifactChecksums $checksumFixture
Remove-Item -LiteralPath (Join-Path $checksumFixture $setupName)
$rejected = $false
try { Assert-ArtifactChecksums $checksumFixture } catch { $rejected = $true }
if (-not $rejected) { throw 'Checksum accepted absent setup artifact.' }
'PASS: checksum rejects absent setup artifact' | Write-Output
[IO.File]::WriteAllLines((Join-Path $checksumFixture 'SHA256SUMS.txt'), [string[]]@($portableLine))
Assert-ArtifactChecksums $checksumFixture
# Real negative fixtures: ensure the payload validator rejects a marker in installed app files.
$wrong = Join-Path $FixtureRoot 'wrong-payload'
New-Item -ItemType Directory -Path $wrong | Out-Null
foreach ($name in $script:AppFiles) { Copy-Item -LiteralPath (Join-Path "$PackageRoot/app" $name) -Destination $wrong }
[IO.File]::WriteAllText((Join-Path $wrong 'portable.flag'), '')
$rejected = $false
try { Assert-AppPayload $wrong } catch { $rejected = $true }
if (-not $rejected) { throw 'Wrong payload accepted.' }
foreach ($bad in @($repo, (Split-Path $repo), $FixtureRoot, 'relative-output')) {
    $rejected = $false
    try { Assert-NewPackageDirectory $bad $repo } catch { $rejected = $true }
    if (-not $rejected) { throw 'Dangerous or existing destination accepted.' }
}
'PASS: allowlist, ZIP paths, payload/hash identity, wrong marker and dangerous output rejection.' | Write-Output
if ($RunSmoke) {
    $foreign = Join-Path $FixtureRoot 'foreign-cwd'
    New-Item -ItemType Directory -Path $foreign | Out-Null
    $guardInfo = [Diagnostics.ProcessStartInfo]::new((Join-Path $extracted 'Threadbook.exe'))
    $guardInfo.UseShellExecute = $false
    $guardInfo.WorkingDirectory = $foreign
    $guardInfo.ArgumentList.Add('--smoke-test')
    $guardProcess = [Diagnostics.Process]::Start($guardInfo)
    try {
        if (-not $guardProcess.WaitForExit(15000)) { $guardProcess.Kill($true); throw 'Explicit fixture guard timed out.' }
        if ($guardProcess.ExitCode -ne 1 -or (Test-Path -LiteralPath "$extracted/Data")) { throw 'Portable marker bypassed explicit test fixture guard.' }
    } finally { $guardProcess.Dispose() }
    'PASS: packaged marker cannot bypass explicit test fixture guard.' | Write-Output
    $info = [Diagnostics.ProcessStartInfo]::new((Join-Path $extracted 'Threadbook.exe'))
    $info.UseShellExecute = $false
    $info.WorkingDirectory = $foreign
    foreach ($arg in @('--data-dir', (Join-Path $extracted 'Data'), '--smoke-test')) { $info.ArgumentList.Add($arg) }
    $process = [Diagnostics.Process]::Start($info)
    try {
        if (-not $process.WaitForExit(90000)) { $process.Kill($true); throw 'Synthetic WPF smoke timed out.' }
        if ($process.ExitCode -ne 0) { throw "Synthetic WPF smoke exit $($process.ExitCode)." }
    } finally { $process.Dispose() }
    if (-not (Test-Path -LiteralPath "$extracted/Data/smoke-result.txt")) { throw 'Missing runtime smoke result.' }
    Get-Content -LiteralPath "$extracted/Data/smoke-result.txt"
}
