# Shared Git worktree workflow

A workbench contains `repository.git`, a shared bare object database. `master` and
feature worktrees under `worktrees\` are real, complete linked checkouts. They do not
duplicate commit history: a commit created in one checkout is immediately visible to
the others.

The tracked content includes worktree/device metadata, complete `exported-source`
baselines, and sparse `modified-source` overlays. Ignore rules cover staging,
per-device SQLite files, and `.automation`.

Approved PLC refreshes stage the exact reconciled paths and commit automatically.
Feature edits are committed on the worktree branch and merged into a clean target
worktree. Ignored runtime artifacts do not make the merge target dirty. Merge history
remains visible from `master`.

