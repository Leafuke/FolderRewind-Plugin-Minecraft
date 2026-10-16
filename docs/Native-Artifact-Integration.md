# Native Artifact integration (2026-10-16)

Approved branch: codex/integrate-native-artifact-1.9, starting at 8ffb5cb85d143f35c1d3f7a6eb0d42cc473a4398 and integrating source 19e11a7. The Host integration plan is docs/plans/Native_Artifact_Integration_1.9_Plan.md in the independent FolderRewind repository.

Only formally released formats have a compatibility promise. Keep the domain Full/checkpoint codec needed by new incremental capture, but not unpublished P2 paired writing. Host owns directory evidence and orchestration. API 3.10 is consumed as a source-hash-versioned pinned NuGet snapshot with provenance and SHA; never reference Host app/source projects or bundle Abstractions DLL into frplugin. Plugin version becomes 1.9.9. Run both adapter and World test projects in CI; package an exact clean staging inventory.

M0 baseline: .NET 10.0.402, 93 adapter tests passed with zero failed/skipped. The current default 3.9.0 package could not restore from public nuget.org; the frozen target Host contract was packaged into an isolated baseline feed/cache, with no product build dependency added. Raw baseline-adapter.trx is in ignored artifacts/integration/baseline.

No push, tag, Release or NuGet publication is authorized. Final local merge to 1.9.x follows complete package/end-to-end/safety acceptance; plugin is merged before Host.
