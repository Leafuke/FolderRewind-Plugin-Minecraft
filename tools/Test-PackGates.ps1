param(
    [Parameter(Mandatory)][string]$BuildOutput,
    [Parameter(Mandatory)][string]$SdkAssembly,
    [Parameter(Mandatory)][string]$SdkPackage,
    [Parameter(Mandatory)][string]$Provenance,
    [switch]$Development,
    [string]$EvidenceDirectory = (Join-Path $PSScriptRoot '../artifacts/pack-gates')
)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$repo = Split-Path $PSScriptRoot -Parent
$root = Join-Path ([IO.Path]::GetFullPath($EvidenceDirectory)) ([guid]::NewGuid().ToString('N'))
$staging = Join-Path $root 'staging'; New-Item -ItemType Directory -Path $staging -Force | Out-Null
$inventory = @('MineRewind.dll','MineRewind.World.dll','fNbt.dll','K4os.Compression.LZ4.dll','System.IO.Hashing.dll','preview-dependencies.NOTICE.txt','manifest.json','settings.schema.json')
foreach ($name in $inventory) { Copy-Item -LiteralPath (Join-Path $BuildOutput $name) -Destination $staging }
Copy-Item -LiteralPath $Provenance -Destination (Join-Path $staging 'contract-source.json')
$output = Join-Path $root 'verified.frplugin'
$tool = Join-Path $repo 'tools/MineRewind.Pack/bin/Release/net10.0/MineRewind.Pack.dll'
if (!(Test-Path -LiteralPath $tool)) { throw 'Build tools/MineRewind.Pack in Release first.' }
$switch = if ($Development) { 'true' } else { 'false' }
$script:currentSdk = $SdkAssembly
$results = [Collections.Generic.List[object]]::new()
function Run-Pack([string]$name, [bool]$success) {
    $messages = & dotnet $tool $staging $output --sdk $script:currentSdk --sdk-package $SdkPackage --allow-development $switch 2>&1
    $code = $LASTEXITCODE
    $messages | Set-Content -LiteralPath (Join-Path $root ($name + '.log'))
    if (($code -eq 0) -ne $success) { throw "Unexpected pack outcome: $name ($code)." }
    if (!$success -and (Get-FileHash -LiteralPath $output).Hash -cne $script:validHash) { throw "Failed validation replaced the valid package: $name." }
    $results.Add(@{name=$name; passed=$true})
}
Run-Pack 'valid' $true
$script:validHash = (Get-FileHash -LiteralPath $output).Hash
if ($Development) {
    $switch = 'false'; Run-Pack 'development-without-opt-in' $false; $switch = 'true'
}
$manifestPath = Join-Path $staging 'manifest.json'; $original = [IO.File]::ReadAllBytes($manifestPath)
foreach ($case in @('product-version','prerelease','api','architecture')) {
    $manifest = [Text.Encoding]::UTF8.GetString($original) | ConvertFrom-Json
    switch ($case) {
        'product-version' { $manifest.version='1.10.99' }
        'prerelease' { $manifest.version += '-rc.1' }
        'api' { $manifest.pluginApi.minor=17 }
        'architecture' { $manifest.architectures=@('x64') }
    }
    $manifest | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath $manifestPath -Encoding utf8
    Run-Pack $case $false
}
[IO.File]::WriteAllBytes($manifestPath, $original)
$badSdk = Join-Path $root 'altered-sdk.dll'; Copy-Item -LiteralPath $SdkAssembly -Destination $badSdk
$stream=[IO.File]::Open($badSdk,[IO.FileMode]::Append); try { $stream.WriteByte(0) } finally { $stream.Dispose() }
$script:currentSdk=$badSdk; Run-Pack 'sdk-hash' $false; $script:currentSdk=$SdkAssembly
Copy-Item -LiteralPath $SdkAssembly -Destination (Join-Path $staging 'FolderRewind.Plugin.Abstractions.dll')
Run-Pack 'bundled-sdk' $false
Remove-Item -LiteralPath (Join-Path $staging 'FolderRewind.Plugin.Abstractions.dll')
$entryPath=Join-Path $staging 'MineRewind.dll'; $entry=[IO.File]::ReadAllBytes($entryPath); $changed=$entry.Clone()
$coff=[BitConverter]::ToInt32($changed,0x3c)+4; $changed[$coff]=0x64; $changed[$coff+1]=0x86
[IO.File]::WriteAllBytes($entryPath,$changed); Run-Pack 'entry-machine' $false; [IO.File]::WriteAllBytes($entryPath,$entry)
foreach ($properties in @(@('/p:Version=1.10.99'), @('/p:PluginApiPackageVersion=3.17.0'))) {
    $messages = & dotnet msbuild (Join-Path $repo 'MineRewind/MineRewind.csproj') /t:VerifyResolvedContract /p:Configuration=Release /p:Platform=x64 "/p:ContractProvenance=$Provenance" "/p:AllowDevelopmentContract=$switch" @properties 2>&1
    if ($LASTEXITCODE -eq 0) { throw "Build property override bypassed the contract gate: $properties" }
    $results.Add(@{name=($properties -join ','); passed=$true})
}
$results | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $root 'results.json')
Write-Output "Passed $($results.Count) package/build gate checks. Evidence: $root"
# The last native invocation above deliberately fails. Do not leak that expected exit status to callers.
$global:LASTEXITCODE = 0
