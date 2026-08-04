# PLC source version-control workflow

A workbench contains one shared Git repository and linked `master` and feature
worktrees. Git tracks only PLC source XML under
`devices/<device>/source/**/*.xml`. `worktree.json`, `device.json`, staging,
SQLite knowledge databases, export manifests, sessions, and recovery evidence
are runtime artifacts and are excluded from PLC history.

## Master and feature worktrees

`master` is the clean project baseline. Ordinary source-editor writes are
rejected there. Users edit source in a feature worktree, where the same source
tree is writable. A newly created feature inherits the master device metadata
and source snapshot without creating an exported/modified overlay pair.

## Refresh and commit

TIA refresh is a two-step operation: stage an export, then compare it with the
source tree and approve individual XML paths. Applying approval writes those
files to the selected worktree but does not stage or commit automatically. The
version-control page selects individual changed XML paths and commits them with
a user-supplied message through the worktree-level commit endpoint.

XML bytes define whether a file is touched. Diff summaries suppress only the
known TIA creation timestamp; protected logic and structure changes remain
visible, while safe header and multilingual text changes are summarized.

## Validation evidence

Validation is permanent evidence attached to a commit as an annotated tag named
`tia-validation/<full-commit-sha>`. Evidence records use schema `1.0` and are
immutable. History marks commits as `Validated`, `Unlabeled`, or `Invalid` and
records whether the evidence came from `tia-sync` or `feature-merge`.

## Unauthorized master changes

If master contains source changes outside the editor policy, the user may move
selected XML files into a newly created feature worktree, with byte hashes
stored under ignored `.automation/recovery`, or explicitly discard selected
paths back to `HEAD`. Neither recovery action creates a master commit. Discard
requires an explicit confirmation.

TIA consistency and validated feature import/merge rules are implemented in
Plans 2 and 3.
