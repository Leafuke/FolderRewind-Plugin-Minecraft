$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$provenance = Get-Content -LiteralPath (Join-Path $repo 'contracts/source-manifest.json') -Raw | ConvertFrom-Json
[xml]$project = Get-Content -LiteralPath (Join-Path $repo 'MineRewind/MineRewind.csproj') -Raw
$declared = @($project.Project.PropertyGroup.PluginApiPackageVersion | Where-Object { $_ })[0].InnerText
if ($declared -cne $provenance.version) { throw 'Contract package version differs from its committed provenance.' }
$package = Join-Path $repo "contracts/feed/FolderRewind.Plugin.Abstractions.$($provenance.version).nupkg"
if ((Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash -ine $provenance.packageSha256) { throw 'Contract package SHA256 differs.' }
Add-Type -AssemblyName System.IO.Compression
$archive = [IO.Compression.ZipFile]::OpenRead($package)
try {
    $assembly = $archive.GetEntry('lib/net10.0/FolderRewind.Plugin.Abstractions.dll').Open()
    try { $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($assembly)) }
    finally { $assembly.Dispose() }
    if ($hash -ine $provenance.assemblySha256) { throw 'Contract assembly SHA256 differs.' }
} finally { $archive.Dispose() }
Write-Output "Verified $($provenance.version) from Host $($provenance.sourceCommit)."
