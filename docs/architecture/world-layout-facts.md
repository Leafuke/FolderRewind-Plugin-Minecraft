# Minecraft layout facts and reading boundaries

`MinecraftPathGrammar` is the shared pure syntax for world-relative roots, dimensions,
component paths (`region`, `entities`, `poi`), region coordinates and external chunk names.
`WorldLayoutReader` produces `WorldLayoutFacts` and `DimensionLayoutFacts`; filesystem
observation is bounded by entry count, accumulated path length and depth. Fixed historical
inventories use `FromInventory` and never fall back to the live Source.

Facts carry identity evidence, candidate dimension roots, component paths, legal region
files, ambiguous candidates and incomplete-read reasons. A fact is not permission to
capture or restore. Identity observation, complete-world capture, display, partial-history
preview and region selection retain their own acceptance policies. Ordinary directories
in a Java configuration remain ordinary; a backup metadata file or a lock plus layout is
weak evidence, not proof of a complete world. Missing data cannot establish semantic absence.

`ResolveDimensionRoots` resolves only explicitly requested dimension identities and returns
root candidates, component directory bindings and layout diagnostics, without a region inventory.
It shares candidate path rules with complete observation, but does not enumerate component
contents or unrelated namespaces/dimensions. Both legacy region parameters and map selections
use it; map selections resolve all selected layers once per world. Unsafe selected roots or
component directories fail, while unselected links, enumeration permissions and entry/depth
budgets cannot block the selection. Identity syntax is validated before filesystem access;
Source aliases remain distinct from discovered child links, and cancellation is propagated.
Actual selected file reads remain subject to Host capture checks. Metadata and full preview
retain their complete-observation budgets and strict/display policies.

Legacy overworld/DIM paths, namespaced 26.x/custom dimensions and Paper sibling dimensions
share discovery rules. Paper siblings must be within the configured Source; association
never expands authority outside that root. An explicit Source alias and a link discovered
below it remain distinct cases under Host path binding rules. Ambiguities and mixed layouts
are diagnostics; nested worlds retain longest matching world-root identity.

Metadata counts only direct, legal `.mca` children of identified dimension component
directories inside the Source. Terrain/entities/poi are all included. Paper paths are
included once, even when more than one candidate points to them. Same-named files in
unrelated or nested directories do not count. Partial reads preserve completed fields and
the lower-bound / unfinished-count diagnostic; they never report a complete count.

This shared implementation does not change serialized source/world/dimension IDs,
region addresses, fingerprints, artifact v1 formats or merge policy versions. In particular,
semantic historical address parsing preserves existing identities rather than rebinding
old Paper or weak-marker inputs according to newly observed live folders.

Legacy read-only materialization and space lifetimes are specified in [Minecraft World Full v1](minecraft-world-full-v1.md).
Read budgets and incremental diagnostics are specified in [read and state v1](../../contracts/minecraft-read-and-state-v1.md).
