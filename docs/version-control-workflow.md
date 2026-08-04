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

## Version control workspace

The Version control tab is worktree-scoped and covers every registered PLC.
Changes shows only source XML objects, grouped by PLC and category. Select
individual objects and enter a commit message; there is no staging concept in
the UI. Direct master edits are shown as unauthorized and cannot be committed
from this screen.

Compare with TIA first checks the saved checksum evidence. A checksum match is
shown immediately. A mismatch runs a full source scan and presents individual
block, DB, UDT, and tag-table differences. Selected supported TIA changes can
be accepted into master, but remain uncommitted until the user records the
change.

For a feature worktree, Prepare feature import creates a three-way import plan.
Objects changed in both TIA and the feature are disabled individually; unrelated
objects remain selectable. Importing is followed by compiling every device and
confirming that the complete PLC software was tested on the machine. Only the
server-issued validation ID can publish the no-fast-forward merge and its
permanent evidence.

History displays changed PLC objects, validation state, checksums, and evidence.
Historical recovery creates a new rollback feature containing selected XML; it
never resets or directly restores master.
