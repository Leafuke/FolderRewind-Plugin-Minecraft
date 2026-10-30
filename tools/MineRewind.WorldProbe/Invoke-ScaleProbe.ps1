param([Parameter(Mandatory)][string]$OutputRoot)
$ErrorActionPreference = 'Stop'
$probeDll = Join-Path $PSScriptRoot 'bin/Release/net10.0/MineRewind.WorldProbe.dll'
$root = [IO.Path]::GetFullPath($OutputRoot)
for ($part = $root; $part; $part = [IO.Path]::GetDirectoryName($part)) {
    if ((Test-Path -LiteralPath $part) -and (((Get-Item -LiteralPath $part -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0)) { throw 'Linked output ancestor.' }
}
if (Test-Path -LiteralPath $root) { throw 'Scale output must be new.' }
New-Item -ItemType Directory -Path $root | Out-Null
$results = @()
foreach ($count in @(32, 320)) {
    $source = Join-Path $root "source-$count"
    New-Item -ItemType Directory -Path (Join-Path $source 'region') -Force | Out-Null
    # Independent literal: slot 0 in sector 2, raw unnamed compound {v: 1}; unused bytes matter.
    $bytes = [byte[]]::new(1MB)
    $bytes[2] = 2; $bytes[3] = 1
    $bytes[8195] = 13; $bytes[8196] = 3
    [byte[]]$nbt = @(10,0,0,3,0,1,118,0,0,0,1,0)
    $nbt.CopyTo($bytes,8197)
    for ($i = 0; $i -lt $count; $i++) { [IO.File]::WriteAllBytes((Join-Path $source "region/r.$i.0.mca"),$bytes) }
    & dotnet $probeDll roundtrip --source $source --source-id scale-fixture --output (Join-Path $root "result-$count")
    if ($LASTEXITCODE -ne 0) { throw "Scale roundtrip failed at $count regions." }
    $report = Get-Content -LiteralPath (Join-Path $root "result-$count/result.json") -Raw | ConvertFrom-Json
    $results += [pscustomobject]@{RegionCount=$count; Report=$report}
}
$results | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $root 'scale.json') -Encoding utf8
