# P0/P1: lossless chunk foundation

Baseline: MineRewind `8ffb5cb`, Host `a320b2b`. Branch: `codex/chunk-semantic-foundation`.

This stage delivers a domain-only codec/index, independent scratch reconstruction,
an experimental three-way comparison report and an offline WorldProbe tool.
It does not change Plugin API, installed plugins, production backup/restore,
History, artifact policy, or the preview contract. No merge result is applied.

Implementation order: contract → index/envelope reader → lossless decomposition
and reconstruction → comparison/tool → automated and real-world acceptance.

Gates: exact file inventory/bytes; shared identity/index for both consumers;
bounded buffers (256 MiB ceiling, 16 MiB decoded chunk limit); real-world
round trips with semantic coverage reported separately; preview regressions
passing; no executable merge plan or production history mutation.

Validation uses independent literal Anvil fixtures, corruption/failure tests,
all existing plugin tests, and offline copies of the authorized 26.1.2,
1.21.8, 1.21.1 and preview worlds. Real data and machine config are never committed.
Failure keeps this work in P1: the Host artifact-transform block stays in place.
