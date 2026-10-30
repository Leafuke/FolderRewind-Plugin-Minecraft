# Public Host contract

MineRewind 1.10.0 pins the published `FolderRewind.Plugin.Abstractions 3.17.0` package and requires Plugin API 3.17. Assembly identity remains `3.0.0.0`.

`NuGet.Config` restores from NuGet.org only. `feed/` retains the exact public nupkg as a provenance verification input, not as a local restore source. `source-manifest.json` records the Host source commit, clean-checkout source digest, public package URL, package SHA-256 and contract DLL SHA-256. `tools/Verify-Contract.ps1` verifies the pinned copy before CI restore.

After testing and publishing a new API candidate, run Host `scripts/release/Export-PublishedPluginContract.ps1 -CandidatePackage <tested.nupkg> -PluginRoot <this-repository>` from the candidate's Host commit. It downloads the public package, requires the same contract DLL as the tested candidate, verifies assembly identity, and updates the exact package, provenance and SDK version together. Registry signing can change the complete nupkg hash without changing its DLL. Commit the new package (force-add if ignored) and remove superseded contract inputs.

Build and test both adapter and World suites using an empty cache and NuGet.org. The plugin has no Host project/source references. The frplugin carries `contract-source.json`, never the Abstractions DLL. Packaging uses isolated staging and rejects an inventory that differs from the approved payload.

See the [maintainer release workflow](https://folderrewind.top/en/docs/architecture/releasing) for candidate publication, public restore, plugin packaging and Host integration. Local preview exports are development inputs and do not establish a public SDK release.
