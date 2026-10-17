# Native Artifact integration (2026-10-16)

Approved branch: codex/integrate-native-artifact-1.9, starting at 8ffb5cb85d143f35c1d3f7a6eb0d42cc473a4398 and integrating source 19e11a7. The Host integration plan is docs/plans/Native_Artifact_Integration_1.9_Plan.md in the independent FolderRewind repository.

Only formally released formats have a compatibility promise. Keep the domain Full/checkpoint codec needed by new incremental capture, but not unpublished P2 paired writing. Host owns directory evidence and orchestration. API 3.10 is consumed as a source-hash-versioned pinned NuGet snapshot with provenance and SHA; never reference Host app/source projects or bundle Abstractions DLL into frplugin. Plugin version becomes 1.9.9. Run both adapter and World test projects in CI; package an exact clean staging inventory.

M0 baseline: .NET 10.0.402, 93 adapter tests passed with zero failed/skipped. The current default 3.9.0 package could not restore from public nuget.org; the frozen target Host contract was packaged into an isolated baseline feed/cache, with no product build dependency added. Raw baseline-adapter.trx is in ignored artifacts/integration/baseline.

No push, tag, Release or NuGet publication is authorized. Final local merge to 1.9.x follows complete package/end-to-end/safety acceptance; plugin is merged before Host.

M1: source history integrated without text conflicts; adapter 93 and World 74 tests passed, zero skipped/failed. Temporary API 3.10 candidate feed is build evidence only; final hash-versioned committed contract snapshot and package integration remain pending.

M7 contract/package checkpoint: MineRewind is 1.9.9 and consumes [3.10.0-native.b11074b8b378896d] through the committed exact NuGet snapshot. Source mapping prevents public-feed substitution, and provenance records Host 574d24e10503f7501f4380ed7b4f69f043073151, canonical source hashes, package/assembly SHA256. Native stable snapshots now preflight and enforce Host byte/entry/actual-volume budgets; ordinary Core snapshots retain their existing policy. The current-world command aliases include LIST_VERSIONS. CI runs adapter and World tests separately and verifies the contract snapshot before restore.

The pack target builds a fresh six-file staging inventory (plugin, World, fNbt, manifest, settings schema, contract provenance), rejects unexpected files and never bundles Abstractions. Adapter 94 and World 74 tests passed, with one additional final snapshot budget test after asynchronous test cleanup, zero skipped/failed. Exact package inventory/version/API were checked; a staged Abstractions DLL was rejected before output publication. The local development frplugin SHA256 is cd3d8dd444f2e77b576871f618b00208cd7cc6e284f2baac6517eb3379c57fbf. Final packaged-plugin production activation/upgrade/capture/restore and all Host acceptance gates remain pending; 1.9.x is not advanced yet.
