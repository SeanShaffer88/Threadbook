$script:AppFiles = @(Get-Content -LiteralPath "$PSScriptRoot/../packaging/payload-manifest.json" -Raw | ConvertFrom-Json).files
$script:PackageVersion = ([xml](Get-Content -LiteralPath "$PSScriptRoot/../Directory.Build.props" -Raw)).Project.PropertyGroup.Version
$script:PortableName = "Threadbook-v$script:PackageVersion-win-x64-portable.zip"
$script:SetupName = "Threadbook-v$script:PackageVersion-win-x64-setup.exe"
function Assert-ArtifactChecksums([string]$Path) {
    $expected = @($script:PortableName)
    $setup = $script:SetupName
    if (Test-Path -LiteralPath (Join-Path $Path $setup) -PathType Leaf) { $expected += $setup }
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $lines = @(Get-Content -LiteralPath (Join-Path $Path 'SHA256SUMS.txt'))
    foreach ($line in $lines) {
        if ($line -notmatch ('^([a-f0-9]{64})  (Threadbook-v' + [regex]::Escape($script:PackageVersion) + '-win-x64-(?:portable\.zip|setup\.exe))$')) { throw 'Invalid checksum entry.' }
        $hash = $Matches[1]; $name = $Matches[2]
        if ($name -cnotin $expected -or -not $seen.Add($name)) { throw 'Unexpected or duplicate checksum entry.' }
        if ((Get-FileHash -LiteralPath (Join-Path $Path $name)).Hash -ine $hash) { throw 'Artifact hash mismatch.' }
    }
    if ($seen.Count -ne $expected.Count) { throw 'Missing checksum entries.' }
}
function Assert-NoPackageLinks([string]$Path) {
    for ($part = [IO.Path]::GetFullPath($Path); $part; $part = Split-Path $part -Parent) {
        if (Test-Path -LiteralPath $part) {
            if ((Get-Item -LiteralPath $part -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Filesystem links are not supported for packaging paths.' }
        }
    }
}
function Assert-NewPackageDirectory([string]$Path, [string]$Repository) {
    if (-not [IO.Path]::IsPathFullyQualified($Path)) { throw 'Destination must be absolute.' }
    $full = [IO.Path]::GetFullPath($Path).TrimEnd('\','/')
    $repo = [IO.Path]::GetFullPath($Repository).TrimEnd('\','/')
    if ($full -eq [IO.Path]::GetPathRoot($full).TrimEnd('\','/') -or $full.Length -lt 4) { throw 'Root destinations are forbidden.' }
    if ($repo.Equals($full, [StringComparison]::OrdinalIgnoreCase) -or $repo.StartsWith($full + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or $full.StartsWith($repo + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Destination must be outside the source repository.' }
    Assert-NoPackageLinks $full
    if (Test-Path -LiteralPath $full) { throw 'Destination must be new; existing output is never overwritten.' }
}
function Assert-AppPayload([string]$Path) {
    Assert-NoPackageLinks $Path
    $entries = @(Get-ChildItem -LiteralPath $Path -Force)
    if ($entries.Count -ne $script:AppFiles.Count) { throw 'Payload inventory differs from the committed allowlist.' }
    foreach ($entry in $entries) {
        if ($entry.PSIsContainer -or $entry.Name -cnotin $script:AppFiles -or ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Unexpected payload entry.' }
    }
}
