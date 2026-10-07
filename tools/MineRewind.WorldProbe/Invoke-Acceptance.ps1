param(
    [Parameter(Mandatory)][string]$SamplesJson,
    [Parameter(Mandatory)][string]$OutputRoot
)
$ErrorActionPreference = 'Stop'
$probeDll = Join-Path $PSScriptRoot 'bin/Release/net10.0/MineRewind.WorldProbe.dll'
if (-not (Test-Path -LiteralPath $probeDll)) { throw 'Build WorldProbe Release first.' }
$runRoot = [IO.Path]::GetFullPath($OutputRoot)
if (Test-Path -LiteralPath $runRoot) { throw 'Acceptance output must be a fresh directory.' }
$samples = @(Get-Content -LiteralPath $SamplesJson -Raw | ConvertFrom-Json)
function Assert-NoLinkedAncestors([string]$path) {
    for ($part = $path; $part; $part = [IO.Path]::GetDirectoryName($part)) {
        if ((Test-Path -LiteralPath $part) -and (((Get-Item -LiteralPath $part -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0)) { throw 'Linked ancestor.' }
    }
}
Assert-NoLinkedAncestors $runRoot
function Read-Inventory([string]$root) {
    $entries = @(Get-ChildItem -LiteralPath $root -Force -Recurse)
    if ($entries | Where-Object { ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 }) { throw 'Linked source entry.' }
    @($entries | Where-Object { -not $_.PSIsContainer } | Sort-Object FullName | ForEach-Object {
        [pscustomobject]@{Path=[IO.Path]::GetRelativePath($root, $_.FullName); Length=$_.Length; Sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash}
    })
}
foreach ($sample in $samples) {
    $source = [IO.Path]::GetFullPath($sample.Path)
    Assert-NoLinkedAncestors $source
    foreach ($pair in @(@($source, $runRoot), @($runRoot, $source))) {
        $relative = [IO.Path]::GetRelativePath($pair[0], $pair[1])
        if (-not [IO.Path]::IsPathRooted($relative) -and $relative -ne '..' -and -not $relative.StartsWith('..\')) { throw 'Source/output overlap.' }
    }
}
New-Item -ItemType Directory -Path $runRoot | Out-Null
$results = @()
for ($i = 0; $i -lt $samples.Count; $i++) {
    $sample = $samples[$i]
    $source = [IO.Path]::GetFullPath($sample.Path)
    $snapshot = Join-Path $runRoot "sample-$i"
    $before = @(Read-Inventory $source)
    foreach ($entry in $before) {
        $destination = Join-Path $snapshot $entry.Path
        New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($destination)) -Force | Out-Null
        [IO.File]::Copy((Join-Path $source $entry.Path), $destination, $false)
    }
    $after = @(Read-Inventory $source)
    $copy = @(Read-Inventory $snapshot)
    $signature = $before | ConvertTo-Json -Depth 4 -Compress
    if ($signature -cne ($after | ConvertTo-Json -Depth 4 -Compress) -or $signature -cne ($copy | ConvertTo-Json -Depth 4 -Compress)) {
        throw "Sample $i changed during copying. This run is invalid."
    }
    $before | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $runRoot "sample-$i-inventory.json") -Encoding utf8
    $output = Join-Path $runRoot "roundtrip-$i"
    & dotnet $probeDll roundtrip --source $snapshot --source-id $sample.SourceId --output $output
    $code = $LASTEXITCODE
    if ($code -eq 2) { throw "WorldProbe execution failed for sample $i." }
    $report = Get-Content -LiteralPath (Join-Path $output 'result.json') -Raw | ConvertFrom-Json
    $results += [pscustomobject]@{ Sample=$i; Label=$sample.Label; ExitCode=$code; Report=$report }
}
$results | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $runRoot 'acceptance.json') -Encoding utf8
$results | Select-Object Sample,Label,ExitCode | Format-Table
