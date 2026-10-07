# WorldProbe — P1 offline verification

WorldProbe references only `MineRewind.World`. It does not load FolderRewind,
activate a plugin, create History facts, or apply a merge.

```powershell
dotnet build tools/MineRewind.WorldProbe/MineRewind.WorldProbe.csproj -c Release
dotnet run --no-build -c Release --project tools/MineRewind.WorldProbe -- roundtrip --source C:/offline/world --source-id example --output C:/probe/run-001
dotnet run --no-build -c Release --project tools/MineRewind.WorldProbe -- index --source C:/offline/world --source-id example --output C:/probe/run-002
dotnet run --no-build -c Release --project tools/MineRewind.WorldProbe -- compare --base C:/offline/base --ours C:/offline/ours --theirs C:/offline/theirs --source-id example --output C:/probe/run-003
```

All inputs must be offline, read-only during the operation. The source ID is
caller-owned and the same for all three sides; internal world-relative IDs keep
multiple world roots distinct. Vanilla dimensions and namespaced dimensions are
recognized, including entities/poi-only dimensions. Ambiguous identities fail;
unrecognized region layouts are preserved but block automatic analysis.

Outputs must be fresh and disjoint from every source; links are rejected.
Nothing overwrites an existing output. Interrupted/failed run directories may
remain for diagnosis; without valid completion records they are not inputs.
The tool never cleans arbitrary paths or edits the input/configuration.

## Reports and exit status

- `snapshot-N/files.jsonl`: original byte manifest and segment references.
- `snapshot-N/regions.jsonl`: bounded component/chunk metadata and payload hashes.
- `snapshot-N/diagnostics.jsonl`: classified failures, opaque fallback and orphan mcc.
- `snapshot-N/objects/`: payload/residual packs, or explicit opaque/raw files.
- `snapshot-N/complete.json`: schema/identity, authenticated manifest digests and counts.
- `reconstructed/`: roundtrip only; rebuild uses scratch, not the source.
- `bundles.jsonl` / `file-changes.jsonl`: compare only, never an executable plan.
- `result.json`: written last after requested verification/analysis completes.

Exit **0** means verification passed with no unsupported region coverage or
conflicts. Exit **1** means completed but requires attention: opaque/unknown
region data, an incomplete external reference, or comparison conflicts.
Exit **2** means execution failed or was canceled.

`Verification.Success` is byte reconstruction only. `InputComplete` conservatively
requires all region structures to be understood; it is not a game consistency
claim. `FullyComparable` is structural/byte comparison coverage, not Minecraft
gameplay correctness. In particular, a zero-byte mca is preserved as
`empty_region_without_header`, not silently assumed to have no chunks.

Bundle decisions distinguish container changes, timestamp changes and compression/
external encoding changes. Raw file changes are also reported; a segmented mca's
raw byte conflict (including its referenced mcc) is marked `ContainerOnly` and informational, while its chunk decisions determine analysis
conflicts. Both branches modifying entities in a dimension requires larger-domain
review. All reports explicitly set executable/applicable merge to false.

No NBT values or player payloads are logged. Paths/hash metadata in output reports
can identify a world and must remain local when using private samples.

## Limits and measurement

Decode is sequential: at most 16 MiB uncompressed NBT per chunk. Gzip/zlib/raw and
external mcc are supported. Bounded NBT framing checks (depth 64, 100,000 tags,
1,048,576 collection length) do not normalize or construct an NBT object tree.
Content hashes are versioned opaque decompressed-byte hashes, not field-level hashes.

Directory inventory is capped at 65,536 filesystem entries and 4 Mi UTF-16 path
characters. Custom source views obey the same file/path budget. Exceeding limits
fails explicitly rather than allocating an unbounded inventory. There are at most
two packs per file, 4,096 segments and 8 Mi characters per manifest record. Comparison
holds only one region group per side, not the entire chunk table.

`DecodeAndCopyBufferBoundBytes` is a derived bound for binary buffers, **not a
measured heap peak or a whole-process limit**. Reports separately include cumulative
allocated bytes, current managed heap, peak process working set, elapsed wall time
and source/scratch/reconstruction stream I/O byte counts. Counts exclude copying
the acceptance input and the final report itself. Metadata and JSON allocations
are bounded separately; the P1 working-buffer/cache target is 256 MiB.

## Repeatable acceptance

`Invoke-Acceptance.ps1 -SamplesJson <local.json> -OutputRoot <fresh-dir>` accepts an
array of `{ "Path": "...", "SourceId": "...", "Label": "..." }`. It copies each
authorized source, compares pre/post/copy inventories and hashes, then invokes the
tool on the copy. This detects ordinary changes; it is not an atomic game snapshot.
Keep sample paths and inventories outside Git.

`Invoke-ScaleProbe.ps1 -OutputRoot <fresh-dir>` generates independent 32/320-region,
32/320-MiB fixtures and runs both roundtrips. Results are synthetic, not game-load
certification or a latency SLA. `MineRewind.World.Tests` covers independent literal
fixtures, source removal, corruption, fault injection, comparison and CLI behavior.

The scratch schema is experimental and disposable. It is not a published backup
format. Do not use it as the only backup of an important world.
