# Workbench project structure

```text
<user-selected-root-or-default>\
  workbench.json                         # identity, registrations, SVN store path, provenance
  repository.git\                        # shared Git object database (bare) — semantic store
  repository.svn\                        # local SVN repository (file://) — native TIA store
  worktrees\
    <worktree>\
      .git                               # link to repository.git
      .gitignore
      worktree.json                      # branch, device IDs, managed TIA/SVN base state
      engineering-state\
        revision.json                    # GIT-TRACKED: svn url+revision, checksums, compile status
      tia\                               # SVN working copy of the worktree's native branch
      devices\
        <device>\
          device.json                    # PLC identity, reconciliation/knowledge/import state
          source\                        # tracked exported PLC XML baseline
          staging\                       # ignored complete temporary export
          plc-knowledge.db               # ignored database for this device only
      .automation\                       # ignored sessions, pending sync/commit records
```

Git tracks only `devices/<device>/source/**/*.xml` and
`engineering-state/revision.json`. `tia/` (the native TIA project working copy)
and `repository.svn/` are never Git-tracked; SVN holds the complete native
project under `native/main` and `native/branches/<feature>`. Workbenches created
before schema `1.2` have no `repository.svn` and no `tia/` store; they keep the
Git-only behavior.

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
