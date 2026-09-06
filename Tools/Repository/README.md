# Repository text checks

Project-owned text describes behavior directly. Keep unrelated game/editor
names, comparison notes and reference downloads out of source, UI strings,
documentation, filenames and commit messages. Required dependency licenses
remain separate obligations and must not be stripped by a cleanup.

Run `node Tools/Repository/check-text.mjs` for tracked and untracked project
files that are not ignored. The web build runs this check automatically.
Enable the included Git hooks in each fresh clone:

```powershell
git config core.hooksPath .githooks
```

The pre-commit hook inspects the complete staged snapshot, including paths and
binary literal strings; unstaged edits cannot hide a staged violation. The
commit-msg hook checks the proposed message. The policy stores normalized
length/rolling-hash/SHA-256 fingerprints, without retaining the restricted
words themselves. SHA-256 confirms rolling-hash matches before rejecting text.

Run `node --test Tools/Repository/check-text.test.mjs` to validate the checker.
New rules can be computed with the exported `fingerprint()` function in memory;
do not commit the original term as a fixture or explanatory example.

The checker catches the registered text, including case/Unicode compatibility
variants and substrings. It cannot identify arbitrary new game names or text
compressed into images, PDFs or archives. Review those assets before publishing.
Hooks are local safeguards and can be bypassed by Git options; keep the build
check and the project instructions in use too.

The same check also enforces repository boundaries through `check-boundaries.mjs`.
It rejects literal machine paths, engine source/API references outside `engine/unity`,
engine assets outside that adapter, studio project references into engine adapters,
source links outside this repository, and private workspace
maps or engine identifiers in sample catalogues. Both working files and the complete
staged snapshot are checked; the existing fingerprints and commit-message check
remain active. Portable environment-derived paths and ordinary web routes are allowed.

Run `node --test Tools/Repository/check-boundaries.test.mjs` for the boundary gates.
This is a text/structure guard, not a proof of asset ownership or a detector for
compressed image contents. Review newly added sample data before publication.
