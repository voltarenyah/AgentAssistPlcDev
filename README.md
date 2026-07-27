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

Existing `%LOCALAPPDATA%\PlcAiAssistant\exports` directories are legacy data. New
workbenches do not migrate, list, modify, or delete them.

For a user-selected custom root, the trusted host validates persisted workbench
metadata and registers the canonical, non-reparse root in
`%APPDATA%\AutomationWorkbench\trusted-workbench-roots.json`. Engineering and source
editor child processes receive only the host-owned registry location; tool arguments
cannot grant themselves a new filesystem root. Unregistered and reparse-point roots
remain sandbox-denied.

The current implementation is backend-only. See the
[future UI plan](buildnote/plan/workbench-project-storage-future-ui.md).
