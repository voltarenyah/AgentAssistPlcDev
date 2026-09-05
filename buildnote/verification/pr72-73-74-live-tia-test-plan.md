# Live TIA Test Plan — PR #72 / #73 / #74 on latest master

Source: merged PRs #72 (`f8fbaba`, fixes #67), #73 (`1a8284f`, fixes #69), #74 (`fae4396`, fixes #68).
All three shipped with incomplete live-TIA verification; this run closes that gap.
Date prepared: (this session). Tester: Ansel.

## Pre-conditions

- Latest master checked out (contains all three merge commits).
- TIA Portal V17 with a real project; ideally:
  - one ordinary PLC,
  - one failsafe CPU (e.g. 1515F-2 PN) with an F-program,
  - networked devices (PROFINET names/IPs, topology, MRP if available).
- STEP 7 Safety **license available** on the test machine — required for the #72 signature read (unlicensed machines yield `no-signature` / `Unavailable`, which is itself a case to check).
- Workbench baseline exists for the project.

## PR #72 — Safety / F-signature (#67)

**Live results 2026-09-02 (test-safety-signature workbench, PEI_SinoARP_Master_V4.1.3, CPU Sino_PEI / 1515F-2 PN):**

- [x] F-CPU detected as safety device — **PASS at backend.** Live `get_plc_checksums` returns `isSafetyDevice: true`. Baseline `revision.json` records `safety.readState: "ok"`.

- [~] F-signature value read — **BLOCKED: no STEP 7 Safety license on this machine.** Live read state is `no-signature` (provider license-gated, matches PR #72 probe findings). The `ok`/signature-value path cannot be tested on this machine.

- [x] UI visibility — **GAP CONFIRMED (user report).** No UI surface shows whether a device is failsafe; the only safety UI is the savepoint timeline badge on SafetyChanged/read-failed. A user cannot tell that detection succeeded. Candidate follow-up issue.
- [x] Without license (if testable on second machine): read state `no-signature`/`read-failed`, compare vs baseline-with-signature reports `Unavailable`, never `Different`, never in-sync.
- [ ] Edit the F-program (licensed) → next compare: `Different` + `SafetyChanged` even when software checksum and XML are unchanged.
- [ ] Savepoint after F-edit: `revision.json` has `safety.readState`; timeline shows amber **Safety change** badge with archive reminder note.
- [ ] `read-failed` case shows **Safety signature unavailable** badge/note.
- [ ] Commit-state tags carry `isSafetyDevice` / `fSignatureReadState` / `fSignature` per device (schema 1.1); old tags still load.
- [ ] Removing safety from the device (or comparing a non-F device against an F baseline) → `Different` + `SafetyChanged`.

## PR #73 — Network configuration fingerprint (#69)

- [ ] `export_hardware_configuration` produces `network-configuration.txt` next to `project.aml`; `hardware/manifest.json` has `networkConfigurationHash`.
- [ ] First compare after upgrade shows `network` artifact as `new` (expected one-time); accept hardware config → subsequent compares clean.
- [ ] Change PROFINET device name or IP → next compare flips hardware consistency to changed.
- [ ] Change topology port connection / IO-system assignment / MRP / OPC UA server interface → compare flips.
- [ ] Known gap: S7/TCP/UDP connection changes remain undetectable — confirm documented behavior, no crash.
- [ ] Capture failure (if inducible) records `networkConfigurationError` on the manifest and does not fail the AML export.

## PR #74 — Content fingerprint (#68)

- [ ] Comment-only edit on a block → `ContentFingerprint` changes, software checksum unchanged; detected/attributed to the right PLC on next commit/compare.
- [ ] Interface edit (add/remove block param) → content fingerprint changes.
- [ ] UDT comment/member edit → fingerprint changes.
- [ ] Tag-table comment edit → NOT detected (documented gap; verify no error).
- [ ] Timeline shows both channels per PLC (software checksum + content fingerprint); history detail shows "Content fingerprint" section.
- [ ] Commit-state tags with `contentFingerprint` load in this build; legacy tags without it still load.

## Cross-cutting

- [ ] `get_context_status` / `compare_context` carry the safety fields.
- [ ] No regression on ordinary (non-F) PLC flows: baseline, compare, savepoint, master-sync.
- [ ] Full test suites still green where runnable (`dotnet test`, studio `npm test -- --run`).
- [ ] Record results (what passed, what failed, license state of the machine) back into the PRs/issues or a buildnote verification entry.

