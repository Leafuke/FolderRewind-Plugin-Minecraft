# Host contract snapshot

`source-manifest.json` pins the API 3.13 prerelease, Host base commit, exact working-tree source digest, package SHA256 and contract assembly SHA256. The exact nupkg is committed in `feed/`; it is a build input, not a publication to a registry. `tools/Verify-Contract.ps1` checks the package and assembly before CI restore.

NuGet source mapping resolves `FolderRewind.Plugin.Abstractions` only from this feed. The plugin retains PackageReference with an exact version range. No Host project/source reference is used. To update the snapshot, review the Host API and its compatibility baseline, rebuild from the recorded source, replace the version/provenance/package together, and run both adapter and World suites.

Packaging copies the approved payload into a new staging directory and rejects extra files. The frplugin includes this contract's provenance, never its DLL.

The current snapshot is `3.13.0-preview.b79fcb6466c9b129`. `sourceState: working-tree-snapshot` means the base commit does not contain the new API by itself. The recorded hashes cover the exact exported source file bytes. The Host export script is `scripts/Export-MergeContract.ps1`; source, nupkg and assembly SHA256 values must agree before packaging.

MineRewind 1.9.18 retains this exact snapshot. Its terrain-first Merge policy is version 2, while conservative remains version 1; policy versioning is separate from the public API. Optional localized diagnostic text uses existing Arguments (`message.default`, `message.<language>`), without a new contract member or dependency on Host presentation code.
