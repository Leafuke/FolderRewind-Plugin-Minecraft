param(
    [string]$ReferencePath,
    [string]$EffectiveVersion,
    [string]$EffectiveSdkVersion,
    [string]$Provenance,
    [string]$AllowDevelopmentContract = 'false',
    [string]$PackagePathOutput
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$canonical = Join-Path $repo 'contracts/source-manifest.json'
$development = $AllowDevelopmentContract -ceq 'true'
if (!$Provenance) { $Provenance = $canonical }
if (!$development -and [IO.Path]::GetFullPath($Provenance) -ine [IO.Path]::GetFullPath($canonical)) {
    throw 'A provenance override requires explicit AllowDevelopmentContract=true.'
}
$contract = Get-Content -LiteralPath $Provenance -Raw | ConvertFrom-Json
$manifest = Get-Content -LiteralPath (Join-Path $repo 'MineRewind/manifest.json') -Raw | ConvertFrom-Json
[xml]$project = Get-Content -LiteralPath (Join-Path $repo 'MineRewind/MineRewind.csproj') -Raw
$declaredSdk = @($project.Project.PropertyGroup.PluginApiPackageVersion | Where-Object { $_ })[0].InnerText
$declaredVersion = @($project.Project.PropertyGroup.Version | Where-Object { $_ })[0].ToString()
if (!$EffectiveVersion) { $EffectiveVersion = $declaredVersion }
if (!$EffectiveSdkVersion) { $EffectiveSdkVersion = $declaredSdk }
if ($EffectiveVersion -cne $manifest.version -or $declaredVersion -cne $manifest.version) {
    throw 'Effective project version must equal the source Manifest product version; version overrides cannot rewrite it.'
}
if ($contract.packageId -cne 'FolderRewind.Plugin.Abstractions' -or $contract.assemblyVersion -cne '3.0.0.0' -or
    $contract.version -cne $declaredSdk -or $EffectiveSdkVersion -cne $declaredSdk -or
    $contract.apiVersion -cne "$($manifest.pluginApi.major).$($manifest.pluginApi.minor)" -or
    $contract.version -cne "$($manifest.pluginApi.major).$($manifest.pluginApi.minor).0") {
    throw 'SDK version, API or assembly identity differs from provenance. Freeze the published SDK via the Host release workflow, or explicitly use an isolated development candidate.'
}
if ($contract.sourceDigestSha256 -notmatch '^[a-fA-F0-9]{64}$' -or $contract.sourceCommit -notmatch '^[a-fA-F0-9]{40}$' -or
    $contract.sources.Count -eq 0 -or $contract.packageSha256 -notmatch '^[a-fA-F0-9]{64}$' -or
    $contract.assemblySha256 -notmatch '^[a-fA-F0-9]{64}$') { throw 'Incomplete SDK provenance.' }
if ($development) {
    if ($contract.sourceState -cne 'development-candidate' -or ![IO.Path]::IsPathFullyQualified($contract.packageSource) -or
        $Provenance -notlike '*.candidate.json') { throw 'Development contracts must be explicitly marked and use an isolated local candidate package.' }
    $package = $contract.packageSource
} else {
    $url = "https://api.nuget.org/v3-flatcontainer/folderrewind.plugin.abstractions/$($contract.version)/folderrewind.plugin.abstractions.$($contract.version).nupkg"
    if ($contract.sourceState -cne 'committed' -or $contract.packageSource -cne $url) { throw 'Formal builds require frozen public SDK provenance.' }
    $package = Join-Path $repo "contracts/feed/FolderRewind.Plugin.Abstractions.$($contract.version).nupkg"
}
if ((Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash -ine $contract.packageSha256) { throw 'SDK package SHA-256 differs from provenance.' }
Add-Type -AssemblyName System.IO.Compression
$archive = [IO.Compression.ZipFile]::OpenRead($package)
try {
    $entry = $archive.GetEntry('lib/net10.0/FolderRewind.Plugin.Abstractions.dll')
    if ($null -eq $entry) { throw 'Contract package lacks its expected assembly.' }
    $stream = $entry.Open()
    try { $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)) }
    finally { $stream.Dispose() }
    if ($hash -ine $contract.assemblySha256) { throw 'SDK package assembly SHA-256 differs from provenance.' }
} finally { $archive.Dispose() }
if ($ReferencePath) {
    if (![IO.File]::Exists($ReferencePath)) { throw 'Exactly one resolved SDK ReferencePath is required.' }
    $identity = [Reflection.AssemblyName]::GetAssemblyName($ReferencePath)
    $version = [Diagnostics.FileVersionInfo]::GetVersionInfo($ReferencePath)
    if ($identity.Name -cne $contract.packageId -or $identity.Version.ToString() -cne $contract.assemblyVersion -or
        $version.FileVersion -cne "$($contract.version).0" -or
        ($version.ProductVersion -cne $contract.version -and $version.ProductVersion -notmatch ('^' + [regex]::Escape($contract.version) + '\+[a-fA-F0-9]{7,64}$')) -or
        (Get-FileHash -LiteralPath $ReferencePath -Algorithm SHA256).Hash -ine $contract.assemblySha256) {
        throw 'Actual resolved SDK DLL identity, product version or SHA-256 differs from provenance.'
    }
}
if ($PackagePathOutput) { [IO.File]::WriteAllText([IO.Path]::GetFullPath($PackagePathOutput), [IO.Path]::GetFullPath($package)) }
Write-Output "Verified SDK $($contract.version) ($($contract.sourceState)), API $($contract.apiVersion), product $($manifest.version)."
