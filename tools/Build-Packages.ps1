param(
    [Parameter(Mandatory)][string]$OutputRoot,
    [string]$Version = '0.3.0-beta.1',
    [string]$IsccPath,
    [string]$BuildRunnerPath,
    [switch]$SkipInstaller
)
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/PackagePolicy.ps1"
$repo = Split-Path $PSScriptRoot
if ($Version -cne '0.3.0-beta.1') { throw 'Version must match the committed 0.3.0-beta.1 payload.' }
Assert-NewPackageDirectory $OutputRoot $repo
if (-not $SkipInstaller -and (-not $IsccPath -or -not (Test-Path -LiteralPath $IsccPath -PathType Leaf))) { throw 'Supply an installed ISCC executable, or explicitly use -SkipInstaller.' }
New-Item -ItemType Directory -Path $OutputRoot | Out-Null
$OutputRoot = [IO.Path]::GetFullPath($OutputRoot)
$publish = Join-Path $OutputRoot 'app'
function Invoke-Recorded([string]$Executable, [string[]]$Arguments, [string]$Name) {
    if ($BuildRunnerPath) {
        & $BuildRunnerPath -ProjectPath $repo -Executable $Executable -ArgumentList $Arguments
        $code = $LASTEXITCODE
    } else {
        & $Executable @Arguments 2>&1 | Tee-Object -FilePath (Join-Path $OutputRoot "$Name.log") | Out-Host
        $code = $LASTEXITCODE
    }
    @{command=$Name;exitCode=$code} | ConvertTo-Json -Compress | Add-Content (Join-Path $OutputRoot 'commands.jsonl')
    if ($code -ne 0) { exit $code }
}
Invoke-Recorded 'dotnet' @('publish', "$repo/desktop/Threadbook.Desktop.csproj", '-c', 'Release', '-r', 'win-x64', '--self-contained', 'false', "-p:Version=$Version", '-p:DebugType=None', '-p:DebugSymbols=false', '-p:ContinuousIntegrationBuild=true', "-p:PathMap=$repo=/_/Threadbook", '-p:UseSharedCompilation=false', '-o', $publish) 'publish'
Assert-AppPayload $publish
$portable = Join-Path $OutputRoot 'portable'
New-Item -ItemType Directory -Path $portable | Out-Null
foreach ($name in $script:AppFiles) { Copy-Item -LiteralPath (Join-Path $publish $name) -Destination $portable }
Copy-Item -LiteralPath "$repo/LICENSE" -Destination "$portable/LICENSE"
Copy-Item -LiteralPath "$repo/packaging/README-portable.txt" -Destination "$portable/README-portable.txt"
[IO.File]::WriteAllText((Join-Path $portable 'portable.flag'), '')
$zipName = "Threadbook-v$Version-win-x64-portable.zip"
Compress-Archive -LiteralPath @(Get-ChildItem -LiteralPath $portable -File | ForEach-Object FullName) -DestinationPath (Join-Path $OutputRoot $zipName)
$artifactNames = @($zipName)
if (-not $SkipInstaller) {
    # /D and /O are supported by Inno Setup 6 and 7. No installer is launched.
    Invoke-Recorded $IsccPath @("/DAppVersion=$Version", "/DPayloadDir=$publish", "/DLicensePath=$repo/LICENSE", "/O$OutputRoot", "$repo/packaging/Threadbook.iss") 'iscc'
    $artifactNames += "Threadbook-v$Version-win-x64-setup.exe"
}
$manifest = foreach ($name in $script:AppFiles) { @{path=$name;required=$true;sha256=(Get-FileHash -LiteralPath (Join-Path $publish $name) -Algorithm SHA256).Hash} }
@{schemaVersion=1;files=@($manifest)} | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $OutputRoot 'app-manifest.json')
$artifactNames | ForEach-Object { "$((Get-FileHash -LiteralPath (Join-Path $OutputRoot $_) -Algorithm SHA256).Hash.ToLowerInvariant())  $_" } | Set-Content (Join-Path $OutputRoot 'SHA256SUMS.txt')
