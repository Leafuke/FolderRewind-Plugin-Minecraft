# Host contract snapshot

`source-manifest.json` pins the API 3.14 prerelease, Host base commit, exact working-tree source digest, package SHA256 and contract assembly SHA256. The exact nupkg is kept as a reviewable build input in `feed/`; it is a build input, not a publication to a registry. `tools/Verify-Contract.ps1` checks the package and assembly before CI restore.

NuGet source mapping resolves `FolderRewind.Plugin.Abstractions` only from this feed. The plugin retains PackageReference with an exact version range. No Host project/source reference is used. To update the snapshot, review the Host API and its compatibility baseline, rebuild from the recorded source, replace the version/provenance/package together, and run both adapter and World suites.

Packaging copies the approved payload into a new staging directory and rejects extra files. The frplugin includes this contract's provenance, never its DLL.

The current snapshot is `3.14.0-preview.e119795b08783604`. `sourceState: working-tree-snapshot` means the base commit does not contain the new API by itself. The recorded hashes cover the exact exported source file bytes. The Host export script is `scripts/Export-MergeContract.ps1`; source, nupkg and assembly SHA256 values must agree before packaging.

MineRewind 1.9.19 uses this P5 snapshot. Its terrain-first Merge policy is version 3, while conservative is version 2; policy versioning is separate from the public API. Optional localized diagnostic text uses existing Arguments (`message.default`, `message.<language>`), without a new contract member or dependency on Host presentation code.

## P5 interaction repair: API 3.15 / MineRewind 1.9.20

Current exact package: `3.15.0-preview.266401d3f6eda90f`. Source digest: `266401d3f6eda90fc8004befeea26f647e2cbd4f5ff552853fa5e0578da480ff`.
Adds optional comparison category metadata and structured source paths. Presentation classification never replaces full payload identities used by capture or merge. The plugin remains independently built from the exact package; no Host project references or bundled Host Abstractions DLL.

## MineRewind 1.9.21

API/package stay `3.15` / `3.15.0-preview.266401d3f6eda90f`. Merge policies advance to `conservative@3` and `terrain-first@4`: version-gated runtime-independent decision identity, exact selected payload reuse/verification. Existing sessions keep their pinned package/policy identities and require reanalysis for the new behavior.
