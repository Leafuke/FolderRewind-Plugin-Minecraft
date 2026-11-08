[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$NuGetConfig = (Join-Path $PSScriptRoot '../NuGet.Config'),
    [string]$Provenance = (Join-Path $PSScriptRoot '../contracts/source-manifest.json'),
    [string]$PackageCache,
    [string]$EvidenceDirectory = (Join-Path $PSScriptRoot '../artifacts/validation'),
    [string]$PackageOutput,
    [switch]$Development
)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$repo = Split-Path $PSScriptRoot -Parent
$evidence = [IO.Path]::GetFullPath($EvidenceDirectory)
New-Item -ItemType Directory -Path $evidence -Force | Out-Null
$NuGetConfig = [IO.Path]::GetFullPath($NuGetConfig)
$Provenance = [IO.Path]::GetFullPath($Provenance)
$developmentValue = $Development.IsPresent.ToString().ToLowerInvariant()
$properties = @('-p:Platform=x64', "-p:AllowDevelopmentContract=$developmentValue", "-p:ContractProvenance=$Provenance")
$oldCache = $env:NUGET_PACKAGES
function Invoke-Checked([string]$Name, [string[]]$Arguments) {
    & dotnet @Arguments 2>&1 | Tee-Object -FilePath (Join-Path $evidence "$Name.log")
    if ($LASTEXITCODE -ne 0) { throw "$Name failed ($LASTEXITCODE)." }
}
Push-Location $repo
try {
    if ($PackageCache) { $env:NUGET_PACKAGES = [IO.Path]::GetFullPath($PackageCache) }
    & (Join-Path $PSScriptRoot 'Verify-Contract.ps1') -Provenance $Provenance -AllowDevelopmentContract $developmentValue
    Invoke-Checked 'restore' (@('restore', 'FolderRewind-Plugin-Minecraft.slnx', '--configfile', $NuGetConfig, '--no-http-cache') + $properties)
    Invoke-Checked 'build' (@('build', 'FolderRewind-Plugin-Minecraft.slnx', '-c', $Configuration, '--no-restore') + $properties)
    foreach ($project in @('MineRewind.Tests', 'MineRewind.World.Tests')) {
        $testProperties = if ($project -eq 'MineRewind.Tests') { $properties } else { $properties | Where-Object { $_ -ne '-p:Platform=x64' } }
        Invoke-Checked $project (@('test', "$project/$project.csproj", '-c', $Configuration, '--no-build', '--no-restore',
            '--filter', 'TestCategory!=PerformanceBenchmark', '--logger', "trx;LogFileName=$project.trx", '--results-directory', $evidence) + $testProperties)
    }
    $pack = @('msbuild', 'MineRewind/MineRewind.csproj', '/t:PackFrPlugin', "-p:Configuration=$Configuration", "-p:PluginRestoreConfig=$NuGetConfig") + $properties
    if ($PackageOutput) { $pack += "-p:FrPluginOutput=$([IO.Path]::GetFullPath($PackageOutput))" }
    Invoke-Checked 'pack' $pack
    $manifest = Get-Content MineRewind/manifest.json -Raw | ConvertFrom-Json
    if (!$PackageOutput) { $PackageOutput = Join-Path $repo "artifacts/MineRewind-$($manifest.version).frplugin" }
    $assets = 'MineRewind/obj/project.assets.json'
    Copy-Item -LiteralPath $assets -Destination (Join-Path $evidence 'project.assets.json')
    Copy-Item -LiteralPath $Provenance -Destination (Join-Path $evidence 'contract-source.json')
    if ($Configuration -eq 'Release') {
        $resolved = Get-Content -LiteralPath $assets -Raw | ConvertFrom-Json
        $contract = Get-Content -LiteralPath $Provenance -Raw | ConvertFrom-Json
        $cacheRoot = @($resolved.packageFolders.PSObject.Properties.Name)[0]
        $sdkRoot = Join-Path $cacheRoot ("folderrewind.plugin.abstractions/" + $contract.version)
        $sdkPackage = if ($Development) { $contract.packageSource } else { Join-Path $repo ("contracts/feed/FolderRewind.Plugin.Abstractions." + $contract.version + '.nupkg') }
        & (Join-Path $PSScriptRoot 'Test-PackGates.ps1') -BuildOutput (Join-Path $repo "MineRewind/bin/x64/$Configuration/net10.0") `
            -SdkAssembly (Join-Path $sdkRoot 'lib/net10.0/FolderRewind.Plugin.Abstractions.dll') -SdkPackage $sdkPackage `
            -Provenance $Provenance -Development:$Development -EvidenceDirectory (Join-Path $evidence 'pack-gates')
    }
    [ordered]@{
        development=$Development.IsPresent; sdk=(& dotnet --version); configuration=$Configuration
        package=[IO.Path]::GetFullPath($PackageOutput); sha256=(Get-FileHash -LiteralPath $PackageOutput).Hash.ToLowerInvariant()
        nugetConfig=$NuGetConfig; nugetConfigSha256=(Get-FileHash -LiteralPath $NuGetConfig).Hash.ToLowerInvariant()
        cache=$env:NUGET_PACKAGES; contract=Get-Content $Provenance -Raw | ConvertFrom-Json
    } | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath (Join-Path $evidence 'build-identity.json') -Encoding utf8
} finally { $env:NUGET_PACKAGES = $oldCache; Pop-Location }
