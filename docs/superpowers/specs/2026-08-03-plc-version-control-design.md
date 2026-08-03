# PLC Source Version Control Design

## Goal

Make version control describe meaningful PLC source history rather than application runtime state. Git should track exported PLC XML objects, feature worktrees should contain proposed source changes, and `master` should remain the accepted source baseline. A permanent validation label must connect an exact master commit to the compiled TIA state for every device in the workbench.

This design replaces the current tracked `exported-source` plus `modified-source` overlay model for newly created workbenches. Existing workbenches will not be migrated.

## Core principles

1. Git tracks PLC source, not workbench machinery.
2. A worktree is the modified version of its branch; there is no second tracked modification overlay.
3. Direct local source editing on `master` is prohibited.
4. TIA divergence is informative during import and testing, not a global blocker.
5. A validated merge is strict: TIA and the prospective merged source must agree exactly across every device.
6. Feature history is preserved through an explicit merge commit.
7. TIA validation evidence is permanent and belongs to the exact commit it validates.
8. Users choose commit scope, import scope, rollback scope, and whether to retain imports after compilation failure.

## Repository model

A workbench has one shared Git repository with a linked worktree for `master` and each feature branch. Each worktree contains one complete source tree for its branch.

Conceptual device layout:

```text
devices/
  <device-id>/
    source/                 tracked PLC XML only
    staging/                ignored temporary TIA exports
    plc-knowledge.db        ignored generated knowledge data
    device.json             ignored application configuration
.automation/                ignored operation and cache state
worktree.json               ignored application configuration
```

The exact physical location of shared workbench configuration may remain implementation-specific, but it must not appear as ordinary Git source history.

### Tracked source scope

Git tracks XML for all supported source objects:

- program blocks;
- function blocks and functions;
- organization blocks;
- data blocks;
- user-defined data types;
- PLC tag tables.

Generated manifests are not tracked. The app derives object identity and properties from XML and may keep a generated index in ignored cache state.

### Ignored scope

Git does not track or show as normal changes:

- staging exports;
- knowledge databases;
- worktree and device configuration;
- generated indexes and caches;
- automation/session state;
- export timestamps or checksum cache files;
- current TIA connection state.

Ignored entries are excluded from the normal Version Control status response. An advanced diagnostics surface may expose them later, but they must not be mixed with PLC changes.

The ignore policy is repository configuration, not tracked source. A generated `.gitignore` must not become another visible worktree change; use the shared repository's internal exclude configuration or an equivalent app-managed mechanism.

### Removal of the modification overlay

`modified-source` is removed. On a feature branch, `source/` is both the editable source and the candidate version. Git compares it with the feature branch's base and commit history. On `master`, `source/` is the accepted baseline and can be updated only by confirmed TIA synchronization or a validated merge.

## XML identity, equality, and differences

Staging keeps the raw TIA export. Before comparison, the app removes only explicitly recognized volatile export timestamps and normalizes line endings. It does not reorder XML, rewrite logic, or ignore any other field.

After this narrow normalization, equality is exact. Any remaining byte difference means the PLC object was touched.

The normalized XML is hashed with a stable algorithm such as SHA-256. The resulting object fingerprint is used for comparisons and validation evidence. This app-calculated fingerprint covers every supported XML object consistently, including tag tables that may not expose a native TIA fingerprint.

The tracked XML may retain the original TIA-compatible representation. Timestamp-only staging differences are never copied into tracked source, so they do not dirty the worktree. When a meaningful source change is accepted, the Version Control UI hides known timestamp noise from its normalized diff.

Object identity consists of the stable device identity, object category, and PLC object path/name represented by the export. Rename detection is not required initially; an apparent rename is reported as an addition plus a deletion.

### Difference presentation

The primary UI reports object-level status: unchanged, changed, added, deleted, or overlapping. For a selected object, it reports recognized header, title, comment, logic, or structure changes where the existing parser can do so safely. The normalized XML line diff remains the exact fallback. The app must not claim a semantic logic explanation when it has only detected XML differences.

## Branch roles and write rules

### Master

`master` is the accepted source baseline. The app disables normal source editing on master. Master changes can originate only from:

1. TIA changes selected and confirmed by the user, followed by a manual commit; or
2. a feature worktree that passes the validated merge gate.

The backend enforces this rule independently of the UI. Confirmed TIA synchronization records the accepted object paths and fingerprints in ignored operation state until they are committed. A master commit action may include only those recorded pending TIA changes.

If tracked XML is changed directly on disk, the app classifies it as an unauthorized master change. It cannot be committed as a normal master update. The user may move it to a new feature worktree or discard it after explicit confirmation.

Direct Git activity outside the app cannot be prevented. If it produces a master commit without validation evidence, the app treats that commit as unlabeled and requires a full TIA scan.

### Feature worktrees

Feature worktrees are the only normal editable source locations. Users may commit one object at a time or commit a selected batch. Automatic source commits are disabled.

Tracked XML changes must be committed before any of them can be imported to TIA. Ignored runtime changes do not affect this cleanliness requirement.

The feature delta is calculated from the feature/master merge base, not merely from the current master tree. This allows master to receive unrelated TIA-originated commits while a feature is in progress without losing either history.

## Validation evidence

### Validated and unlabeled commits

A master commit may be:

- **validated**: its source exactly matched a successfully compiled TIA state and it has permanent validation evidence;
- **unlabeled**: it contains legitimate history but does not claim complete agreement with TIA.

Selective TIA synchronization can produce an unlabeled master commit when the user accepts only some TIA differences. This supports deliberate partial synchronization without creating false evidence. An unlabeled head disables the checksum fast path and requires a full comparison.

### Internal annotated validation tag

Permanent evidence is stored as an annotated Git tag under an app-owned namespace such as:

```text
tia-validation/<validated-commit-sha>
```

The tag points to exactly one master commit and contains a versioned structured record with:

- validation schema version;
- evidence kind (`feature-merge` or `tia-sync`);
- validated commit SHA;
- workbench identity;
- source feature/worktree identity when applicable;
- confirmation timestamp;
- confirming Git user;
- machine-validation confirmation for feature-merge evidence;
- every device identity and TIA project identity;
- every device's compiled project checksum;
- every supported object's normalized identity and fingerprint.

The Version Control UI presents this as a TIA validation label rather than as a normal user release tag. The app does not allow a validation tag to be replaced or deleted. A later TIA state requires a new master commit and a new tag.

Validation tags remain in the shared bare repository and travel with repository history. If remotes are added later, the app must explicitly include the validation-tag namespace in its fetch/push policy.

### Transactional publication

A validated merge must not intentionally publish a master commit without its tag. The implementation prepares the merge commit and validation record away from the master ref, verifies that master still points to the expected commit, then publishes the master update and validation reference as one guarded operation or with equivalent recovery behavior. Failure before publication leaves master unchanged. Any unexpected partial ref update is reported as a recoverable repository fault and never represented as validated.

## Comparing master with TIA

Comparison is performed at worktree level and covers every configured device.

### Fast gate

If the current master commit has a validation tag:

1. read the current TIA project checksum for every device;
2. compare each checksum with the corresponding stored checksum;
3. if all match, report the workbench as consistent without exporting every object.

If one device checksum differs, only matching devices may skip detailed scanning. The workbench as a whole is not reported consistent until all devices match. If master is unlabeled, all devices require a full scan.

### Full scan

For each device requiring inspection:

1. capture its starting project checksum;
2. export all supported XML objects into ignored staging;
3. normalize and fingerprint each object;
4. capture the project checksum again;
5. discard the result if the checksum changed during export;
6. compare the object set and fingerprints with current master.

The result identifies changed, added, deleted, and unchanged objects. A scan does not modify tracked source.

### Selective TIA synchronization

The user selects individual TIA differences to accept into master. Confirmed selections are copied from staging into tracked source and recorded as authorized pending TIA changes. The user then selects objects, enters a commit message, and commits manually.

Unselected differences remain visible as ignored divergence for the current operation. They do not block feature import or testing. A commit representing only selected differences is unlabeled.

If all differences are resolved, every device has a valid compiled checksum, and committed source exactly represents the unchanged TIA scan, the current master commit may receive `tia-sync` validation evidence using the per-device checksums and fingerprints. The user's source-acceptance confirmation is recorded; this evidence does not claim a separate machine-validation exercise. Intermediate per-object commits remain unlabeled; only the exact final state is labeled.

## Feature import and testing

### Preconditions

Before a feature import:

- the feature's tracked XML is clean and committed;
- TIA is compared with current master through the fast gate or full scan;
- the user has reviewed any TIA divergence;
- the requested feature objects are checked for overlap.

TIA divergence does not globally block import. The user may accept selected TIA changes into master or deliberately ignore them for the testing session.

### Three-way overlap rule

For each feature object, compare:

- the feature/master merge-base version;
- current master;
- current TIA;
- the committed feature version.

If both the accepted/current TIA side and the feature changed the same XML object from the shared base, that object is non-importable. No automatic semantic merge is attempted. The UI explains the overlap and disables only that object. Unrelated objects remain importable.

### Import behavior

The user selects committed, non-overlapping feature objects to import. The app records the feature commit, device, object identity, prior master fingerprint, and import outcome in ignored operation state.

Import is reported per object. A partial failure does not pretend the batch was atomic and does not prevent unrelated successful objects from being tested.

### Compile and machine testing

After import, the user controls compilation and machine testing. A compile failure prompts the user to either:

- keep the imported objects for further work, including importing missing dependencies; or
- roll selected successful imports back to their current master versions.

Compilation failure does not automatically roll back because it may be caused by another missing piece. It always prevents validated merge eligibility.

## Validated feature merge

A worktree-level merge always considers every device, including devices without an intended feature change.

### Prospective merge

Before changing master, the app builds the prospective merge result from current master and the feature branch. It must preserve feature commits and detect Git/object conflicts. The prospective tree is the source state that TIA must match; comparing only with current master would incorrectly report the imported feature itself as divergence.

### Merge gate

The merge can proceed only when all of the following are true:

1. every feature XML change is committed;
2. the prospective merge can be produced without an unresolved object conflict;
3. every feature source change in that result exists in TIA;
4. TIA contains no additional unresolved source difference;
5. the entire PLC software compiles successfully for every device;
6. every device returns a valid project checksum;
7. the user confirms machine validation;
8. a final full export of every device matches the prospective merge tree exactly;
9. each device checksum remains unchanged across that final export.

Ignored TIA divergence may coexist with feature import and testing, but it blocks this final gate. Every difference must be accepted into master/feature as appropriate or reverted in TIA before validation. This prevents a checksum for TIA state `A + B` from being attached to a master commit containing only feature `B`.

### Successful merge

On success, the app creates an explicit no-fast-forward merge commit on master, preserving all feature commits and making the validation boundary visible. It then publishes the immutable validation tag containing every device checksum and every object fingerprint.

After publication, current TIA and the new master commit describe the same compiled multi-device source state. Future comparisons may use the checksum fast gate.

### Failed gate

If any device fails compilation, lacks a valid checksum, changes during final export, or differs from the prospective tree, the entire worktree merge is withheld. Master and the feature branch remain unchanged. Imported TIA objects may remain for correction and another validation attempt.

## Rollback and historical recovery

### Testing rollback

During feature testing, the current master XML is the rollback source for an imported existing object. The user selects which objects to restore and confirms any known TIA differences that would be overwritten. This changes TIA but not Git history.

### Rollback after a validated merge

Master is never reset backward. To undo an accepted historical change:

1. select a previous commit or historical object versions;
2. create a rollback feature worktree;
3. commit the selected reversions on that feature;
4. import and test them in TIA;
5. compile every device and complete the normal validation gate;
6. merge the rollback feature as a new validated merge commit.

History therefore records both the original change and the validated rollback.

## Version Control page

The page presents PLC concepts rather than raw repository machinery.

### Top status

Show:

- current worktree and branch;
- master or feature role;
- uncommitted source-object count;
- TIA state: consistent, different, scan required, scanning, or unavailable;
- validation state: validated or unlabeled;
- worktree-level merge readiness across all devices.

### Changes view

- List only tracked PLC XML, grouped by device and object type.
- Use PLC object names as the primary labels, with file paths as secondary details.
- Allow selecting individual objects or batches.
- Commit the selected objects directly with a user-entered message.
- Do not expose a separate persistent stage/unstage workflow.
- Leave unselected objects uncommitted.
- On master, distinguish authorized pending TIA changes from unauthorized local edits.

### Compare with TIA view

- Display whether the checksum fast gate passed.
- For a full scan, list object-level changes, additions, and deletions.
- Allow selected TIA changes to be accepted into master or ignored for the current operation.
- On feature branches, show importable, imported, failed, rolled-back, and overlapping objects.
- Disable only actions affected by a conflict or missing precondition.

### History view

- Show individual feature commits and explicit merge commits.
- Mark validated master commits with a TIA validation label.
- Mark unlabeled master commits as requiring a full scan.
- Make commits selectable and show the actual changed PLC objects.
- Show per-device checksum and validation evidence for a validated commit.
- Replace destructive master restore with `Create rollback feature`.

### Right dock

For the selected object, commit, or validation label, show the relevant subset of:

- device and TIA project identity;
- PLC object type and path;
- working, base, master, feature, and TIA fingerprints;
- normalized change summary and exact XML line diff;
- overlap or non-importable reason;
- commit author, message, and changed object list;
- per-device compiled checksum and validation confirmation;
- available actions for the current state.

## Operation and failure behavior

- Comparison and fingerprinting never alter tracked source.
- Source synchronization copies only explicitly confirmed objects.
- Commit operations stage and commit exactly the selected paths; unselected paths are untouched.
- There is no automatic commit after TIA export, source refresh, or edit.
- Import reports outcomes per object and preserves a recoverable record for the current session.
- Compile failure creates no Git commit, branch update, or validation tag.
- Merge validation rechecks branch tips before publication to prevent racing changes.
- A failed merge leaves master and feature branch refs unchanged.
- Validation tags are never inferred from a successful Git merge alone.
- Missing, malformed, duplicate, or mismatched validation tags are treated as invalid evidence and force a full scan.
- One blocked object does not block unrelated imports; one failed device does block the final worktree-level merge.

## Initial capability boundary

The first implementation detects additions, deletions, and apparent renames but does not import or roll them back. These actions require a separate investigation of TIA Openness behavior and safe recovery semantics. Until then, such objects are clearly marked unsupported and prevent an exact validated merge if the prospective result depends on them.

The initial implementation also excludes:

- semantic merging of generated XML;
- automatic conflict resolution;
- existing-workbench migration;
- Git remotes and remote collaboration policy;
- rewriting or deleting historical validation evidence.

## Verification and acceptance

### Repository and status

- A newly created workbench is clean after initialization.
- Only supported PLC XML appears in Git history and normal status.
- Staging, databases, configuration, and automation state never appear as source changes.
- Feature worktrees contain one complete source tree and no `modified-source` overlay.
- Timestamp-only TIA exports do not dirty the tracked source.
- Any other normalized XML difference marks the object changed.

### Commit behavior

- A user can select and commit one object or a batch with a manual message.
- Unselected changes remain uncommitted.
- Feature import is rejected while tracked feature XML is uncommitted.
- Normal source writes and unauthorized commits on master are rejected.
- Confirmed TIA changes can be committed manually on master.

### Compare and synchronization

- Matching checksums for every device complete the fast gate without full export.
- A mismatching or missing checksum triggers the required object-level scan.
- A checksum change during export invalidates the scan.
- Selective TIA acceptance creates correct pending XML and may produce an unlabeled commit.
- Exact synchronization can label the matching committed master state.

### Feature import and rollback

- Three-way overlap disables only the overlapping object.
- Non-overlapping committed objects remain selectable and importable.
- Partial import outcomes are represented accurately.
- Compile failure offers keep or selected rollback and never creates validation evidence.
- Testing rollback restores selected existing objects from current master without changing Git.

### Validated merge

- The prospective merged tree, rather than current master alone, is compared with TIA.
- A failure in any device's full compilation prevents merge.
- Missing checksums, extra TIA changes, missing feature changes, conflicts, or scan races prevent merge.
- A successful multi-device gate creates one explicit merge commit preserving feature history.
- The matching immutable validation tag contains every device checksum and object fingerprint.
- The next comparison can pass through the checksum fast gate.
- A later TIA state cannot overwrite the old evidence and requires a new commit and label.

### History and recovery

- History shows real changed objects for commits and validation status for master commits.
- Selecting a validation label exposes per-device evidence in the right dock.
- Historical restore creates a rollback feature and does not reset master.

## Documentation transition

When implementation begins, `docs/version-control-workflow.md` must be updated to replace its current tracked metadata, `exported-source`, `modified-source`, and auto-commit description with this approved model. Until that implementation lands, this document is the target design rather than a claim about current runtime behavior.
