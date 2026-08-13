# TIA Portal project-functions implementation plan

Source: `C:\Users\Ansel\orca\projects\anydoc\md\TIAPortalOpenness_Projects_and_Project_Data.md`.

## P0 status and real-app test matrix

Implemented in this worktree:

- Project capability and expanded project metadata reading.
- Normal open, upgrade-aware open, primary/secondary open-mode plumbing, and authentication-mode plumbing.
- Project, worktree, and device context-menu actions.
- Read-only TIA access report dialog.
- Archive TIA project dialog with a native destination-folder picker.
- Idempotent TIA open flow: reuse a matching active connection and switch only when a different project is requested.

Real-app tested against `NewProject/master/PLC_1` and its `tia.ap17` project:

- [x] Worktree services and live UI load.
- [x] Project context menu contains the project-scoped TIA actions.
- [x] Worktree context menu contains the worktree-scoped TIA actions.
- [x] Device context menu contains the device-scoped TIA actions.
- [x] Device → Inspect TIA access opens the real TIA V17 project.
- [x] TIA Openness authorization prompt handled with one-time approval.
- [x] Real project metadata and capability report displayed in the UI.
- [x] Worktree Archive TIA project menu opens the archive dialog with a native folder-picker action.
- [x] Live TIA V17 archive endpoint tested against the connected `NewProject/master` project; `.zap17` output created successfully.
- [x] Re-open request tested against the already attached live TIA session; operation completed successfully without requiring a manual disconnect.
- [x] Engineering session disconnected after inspection; no active session left behind.

Real-app tests still required or intentionally deferred:

- [ ] Open TIA with UI: requires leaving a visible TIA session open for verification.
- [ ] Open with upgrade: requires a project created by an older compatible TIA version.
- [ ] Secondary project mode: requires opening a second project as secondary.
- [ ] Protected-project SSO, anonymous, interactive, and credential paths: require matching project/authentication setup; plaintext credentials remain unsupported at the MCP boundary.
- [ ] Write operations, including save and compile: require explicit mutation test approval and a disposable project copy.

P1 disposable real-app probe status:

- [ ] Create/archive/retrieve real project lifecycle flow. **Archive step verified separately** against the connected `NewProject/master` project; create and retrieve still require a disposable project probe.

## P1 implementation order

### P1.1 Project lifecycle foundation — current slice

- [x] Add contracts and MCP tools for creating a project through the connected Openness portal.
- [x] Add contracts and MCP tools for archiving the currently open project.
- [x] Classify lifecycle tools in the sandbox policy.
- [x] Add contract tests before implementation.
- [x] Add engineering API and TypeScript client wiring after the engineering surface stabilized.
- [x] Add the archive user experience: worktree context-menu action plus destination-directory, archive-name, and archivation-mode dialog. The action opens the selected worktree project in TIA before archiving it.
- [x] Fix archive sandbox validation so the destination directory is jailed while the filename remains a filename-only argument.

### P1.2 Project lifecycle continuation

- [ ] Retrieve an archive into a target directory with explicit primary/secondary and upgrade options. **Current next slice.**
- [ ] Add a dedicated close-project operation distinct from releasing an attached session.
- [ ] Exercise create/archive/retrieve on disposable project data.

### P1.3 Project data and settings

- [ ] Expose project language state and controlled language changes.
- [ ] Expose general settings needed by the app, including search-index and UI-language settings.
- [ ] Expose object/composition structure and selected attributes as read-only data.
- [ ] Add multilingual project/device text read and controlled write operations.

### P1.4 Diagnostics and verification

- [ ] Add system-diagnostics export/import with explicit file targets and sandbox classification.
- [ ] Test compile/save/diagnostic operations against disposable copies only.
- [ ] Update this matrix with real-app outcomes after each testable function.
