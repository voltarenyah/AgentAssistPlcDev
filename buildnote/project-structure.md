# Workbench project structure

```text
<user-selected-root-or-default>\
  workbench.json                         # workbench identity and registrations
  repository.git\                        # shared Git object database (bare)
  worktrees\
    <worktree>\
      .git                               # link to repository.git
      .gitignore
      worktree.json                      # branch, engineering project, device IDs
      devices\
        <device>\
          device.json                    # PLC identity, reconciliation/knowledge/import state
          exported-source\               # complete tracked PLC baseline
          modified-source\               # tracked sparse files to import back
          staging\                       # ignored complete temporary export
          plc-knowledge.db               # ignored database for this device only
      .automation\sessions\              # ignored worktree chat sessions
```

The default parent is `%LOCALAPPDATA%\AutomationWorkbench\Project`; the final
directory is the sanitized user-provided workbench name. A caller may instead pass an
absolute custom root. Persisted IDs, not display names, connect each metadata level.
Registered relative paths are containment checked and existing reparse points are
rejected.

Custom roots are added to a host-owned trusted-root registry only after their
persisted workbench identity is loaded or created through `WorkbenchCatalog`. The
registry path is passed to engineering/source-editor child processes at startup.
Neither MCP tool arguments nor model output can register a root. Registry reads
discard malformed, missing, and reparse-point entries.

Every linked worktree is a complete editable checkout. The bare repository is shared
storage, so commits made in one worktree are visible from every other worktree and can
be merged into `master`.

There is intentionally no migration from `%LOCALAPPDATA%\PlcAiAssistant\exports`.
