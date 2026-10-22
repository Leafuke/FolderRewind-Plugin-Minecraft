# Host contract snapshot

`source-manifest.json` pins the API 3.11 prerelease, Host source commit, canonical source digest, package SHA256 and contract assembly SHA256. The exact nupkg is committed in `feed/`; it is a build input, not a publication to a registry. `tools/Verify-Contract.ps1` checks the package and assembly before CI restore.

NuGet source mapping resolves `FolderRewind.Plugin.Abstractions` only from this feed. The plugin retains PackageReference with an exact version range. No Host project/source reference is used. To update the snapshot, review the Host API and its compatibility baseline, rebuild from the recorded source, replace the version/provenance/package together, and run both adapter and World suites.

Packaging copies the approved payload into a new staging directory and rejects extra files. The frplugin includes this contract's provenance, never its DLL.
