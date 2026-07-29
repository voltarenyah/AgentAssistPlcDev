# Automation Workbench backend

The backend manages PLC engineering source as explicit workbench projects. A user
chooses a workbench name and may choose its root directory. When no root is supplied,
the default is:

```text
%LOCALAPPDATA%\AutomationWorkbench\Project\<workbench-name>
```

Each workbench owns one shared bare Git repository and one or more complete linked
worktrees. Each engineering device has an independent baseline, sparse edit overlay,
staging area, metadata, and knowledge database:

```text
<workbench>\
  workbench.json
  repository.git\
  worktrees\
    master\
      worktree.json
      devices\
        <device>\
          device.json
          exported-source\
          modified-source\
          staging\
          plc-knowledge.db
      .automation\sessions\
```

`exported-source` and `modified-source` are Git tracked. `staging`,
`plc-knowledge.db`, and `.automation` are ignored. A refresh exports completely into
staging, produces a content preview, and changes the tracked baseline only after user
confirmation. Approved changes are committed automatically without rewriting
unchanged files.

`modified-source` contains only files edited in that worktree. These overlays remain
for the worktree lifetime after PLC import and compile. Update the device knowledge
database once after a batch of edits and before relying on it again.

## Offline workflow and TIA synchronization

Normal block browsing, overlay editing, Git work, and knowledge queries use the
persisted device artifacts and do not require TIA Portal. Closing TIA or restarting
the application does not clear `exported-source`, `modified-source`, Git history, or
`plc-knowledge.db`. The block index is reconstructed from the tracked
`exported-source/metadata.json` and merged with sparse overlays.

The device overview reports knowledge state from disk and `device.json`:

- `missing`: `plc-knowledge.db` does not exist.
- `stale`: the database exists, but persisted metadata records baseline or overlay
  changes that have not been ingested.
- `current`: the database exists and no persisted stale flag is set.

Use **Open project in TIA** before an explicit **Compare with TIA** or **Import &
compile** operation. Compare exports the live PLC into temporary `staging`, then
shows stored and live fingerprints. It is non-destructive: tracked baseline files,
overlays, Git history, and the knowledge database remain unchanged until the
engineer explicitly approves selected baseline changes. Import & compile is also an
explicit action and sends only the selected modified source to TIA.

Existing `%LOCALAPPDATA%\PlcAiAssistant\exports` directories are legacy data. New
workbenches do not migrate, list, modify, or delete them.

For a user-selected custom root, the trusted host validates persisted workbench
metadata and registers the canonical, non-reparse root in
`%APPDATA%\AutomationWorkbench\trusted-workbench-roots.json`. Engineering and source
editor child processes receive only the host-owned registry location; tool arguments
cannot grant themselves a new filesystem root. Unregistered and reparse-point roots
remain sandbox-denied.
