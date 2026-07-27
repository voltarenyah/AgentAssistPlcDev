# Device knowledge workflow

Each device owns `<device>\plc-knowledge.db`; databases are not shared across devices
or worktrees and are ignored by Git.

A full rebuild reads the tracked `exported-source` manifest as the authoritative
component list, substitutes matching files from sparse `modified-source`, includes
validated overlay-only components, and writes only that device database.

Editing an overlay marks the device knowledge state stale. Do not update after every
individual edit. Once a related batch is finished, call `update_components` once with
the changed relative paths before reusing the database. Component provenance enables
transactional replacement of the old component graph while retaining graph data
still owned or referenced by other components. Baseline refreshes require a full
rebuild; successful updates persist applied hashes and clear stale state.

