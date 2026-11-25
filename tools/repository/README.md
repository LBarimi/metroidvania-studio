# Repository text checks

Project-owned text describes behavior directly. Keep unrelated game/editor
names, comparison notes and reference downloads out of source, UI strings,
documentation, filenames and commit messages. Required dependency licenses
remain separate obligations and must not be stripped by a cleanup.

From a Git checkout with Node.js 24+, run `node tools/repository/check-text.mjs` before committing. It checks tracked files and untracked files that Git has not ignored. The web build also runs this check. Git hook and workflow configurations are optional local files.

Use `node tools/repository/check-text.mjs --staged` to inspect the complete staged
snapshot, including paths and binary literal strings. A local commit-msg hook
can run `node tools/repository/check-text.mjs --message <file>` on the proposed message. The policy stores normalized
length/rolling-hash/SHA-256 fingerprints, without retaining the restricted
words themselves. SHA-256 confirms rolling-hash matches before rejecting text.

Run `node --test tools/repository/check-text.test.mjs` to validate the checker.
New rules can be computed with the exported `fingerprint()` function in memory;
do not commit the original term as a fixture or explanatory example.

The checker catches the registered text, including case/Unicode compatibility
variants and substrings. It also inspects GIF and PNG metadata and text inside
supported ZIP, TAR and gzip archives. Text drawn into image pixels and content
inside PDFs still need manual review.
Hooks are local safeguards and can be bypassed by Git options; keep the build
check and the project instructions in use too.

The same check also enforces repository boundaries through `check-boundaries.mjs`.
It rejects literal machine paths, engine source/API references outside `integrations/unity`,
engine assets outside that adapter, studio project references into engine adapters,
source links outside this repository, and private workspace
maps or engine identifiers in sample catalogues. Both working files and the complete
staged snapshot are checked; the existing fingerprints and commit-message check
remain active. Portable environment-derived paths and ordinary web routes are allowed.

Run `node --test tools/repository/check-boundaries.test.mjs` for the boundary gates.
This is a text/structure guard, not a proof of asset ownership or a detector for
compressed image contents. Review newly added sample data before publication.
