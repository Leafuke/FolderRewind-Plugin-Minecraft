# Experimental lossless world model (P1)

Three independent claims: bytes preserved, content comparable, input complete.
A corrupt file may satisfy the first, but never implies the latter two.

Identity is caller-owned source ID + world-relative ID + canonical dimension +
chunk X/Z + Terrain/Entities/Poi. Paths are access locations, not identity.
Absence requires a complete inventory. Unsupported/corrupt/unavailable data is
not absence. Deletion is a comparison outcome, not a read state.

Exact reconstruction preserves file names and bytes (not filesystem timestamps,
ACLs or sparse allocation). Normal regions are split into original compressed
payload slices and container residual bytes: header, timestamp table, envelopes,
padding, unused sectors and tails. Reconstruction never re-encodes NBT/compression.
External and orphan mcc files are preserved independently. Malformed/unsupported
regions use explicitly counted opaque whole-file fallback.

Raw files, packs and segments use SHA-256. Comparable payloads hash the complete
decompressed NBT bytes with a versioned domain prefix. No field canonicalization,
surface projection, or DataVersion-based guess is allowed. Format validation is
bounded and independent of the rendering DataVersion whitelist.

Scratch is an internal, versioned, disposable format, not a released backup
representation. Its completion record is written last and authenticates the
manifest; every read validates paths, coverage, lengths and hashes. Hashes detect
corruption, not a malicious author capable of replacing an entire scratch tree.

Inputs are read-only offline views. Filesystem read locks and inventory/hash
rechecks detect ordinary changes; they do not establish an atomic game snapshot.
The tool creates only fresh, disjoint output directories and never applies a merge.

Three-way comparison operates on complete component bundles. Any unknown blocks
automatic classification. Both branches changing entities in one dimension
requires dimension-level review, even if per-chunk changes appear independent.
Output is analysis only, not a guarantee of game-level or mod-level consistency.

Public Plugin API remains unchanged. Future text providers need not implement
Minecraft types, spatial preview, or a custom artifact representation.
