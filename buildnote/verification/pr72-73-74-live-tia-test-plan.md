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
- [~] F-signature value read — **ROOT CAUSE FIXED 2026-09-02 (same-day follow-up), live-verified.** The "license-gated" diagnosis was wrong: `SafetySignatureProvider` is anchored on each **F-block** (manual §5.27.4), never on the DeviceItem. `ReadSafety` now folds per-block `BlockOfflineSignature` values (SHA-256); live read on Sino_PEI returns `ok` with a real signature, shown in the Device properties Safety section. Open probe item: whether per-block reads work without a Safety license.
- [x] UI visibility — **FIXED 2026-09-02 (this follow-up).** The Device properties right dock now has a Safety section (Failsafe device / F-signature state / F-signature) fed by the export manifest's device section. Verified live on Sino_PEI: "Failsafe device: Yes", "No signature (Safety license required)". A reconciler gap that blocked metadata-only manifest updates on refresh-apply was fixed in the same change.
- [ ] Without license (if testable on second machine): read state `no-signature`/`read-failed`, compare vs baseline-with-signature reports `Unavailable`, never `Different`, never in-sync.
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

## Follow-up session 2026-09-02/03 — safety evidence completion (same workbench)

Done and verified live on Sino_PEI (1515F-2 PN):

- **Device properties Safety section** (right dock): Failsafe device / F-signature state / F-signature, fed by the export manifest device section (`isSafetyDevice`, `fSignatureReadState`, `fSignature` — additive, legacy manifests hide the section). Playwright-verified.
- **F-signature root cause fixed:** per-block `BlockOfflineSignature` reads (F-block anchor, manual §5.27.4), folded SHA-256 per PLC. Live values recorded (e.g. `Output=A9FB2639`, `FOB_SAFETY=C0CD89C2`; several blocks `00000000` = uncompiled/invalidated per the manual). F-block detection is now semantic (provider presence) — the name/language heuristics were removed.
- **Per-block recording:** commit-state tags carry `fBlockSignatures` per device (verified in tag `tia-state/5b6ca12…`, all 55 F-blocks with path + signature); revision.json gained `safety.devices[]` with the same data, written at baseline/savepoint.
- **Compare attribution:** `DeviceSafetyEvidence` gained `changedBlocks` (per-block diff when both sides recorded maps; fold-level fallback otherwise); the version-control compare panel shows a "Safety program changed (F-signature)" card listing the changed F-block paths.
- **Reconciler fix:** refresh-apply now propagates document-level manifest changes (device section) even with zero approved component paths, without falsely marking knowledge stale.
- **Aggregator fix:** `no-signature` no longer collapses to `ok` in `AggregateFSignatureReadState`.
- Live compare after the baseline commit: `Different` + `safetyChanged: true` with `changedBlocks: null` — expected one-time transition because the revision.json baseline predates per-block records.

Still open (need live TIA time):

- A real savepoint (with source change or F-edit) that rewrites revision.json with `safety.devices[]` — the message-only commit path records the tag but does not rewrite revision.json. Until then, compares report a one-time safety change with "block-level detail unavailable".
- F-program edit → recompile → compare must name the edited block(s) in `changedBlocks` (unit-tested; live check pending).
- Per-block signature read on a machine WITHOUT a STEP 7 Safety license (untested; reads needed no license here, but only mutating safety actions are documented license-gated).
- PR #73 (network fingerprint) and PR #74 (content fingerprint) live items above remain untouched.
- Note: two message-only commits (`6281ca1`, `5b6ca12`) exist in the test-safety-signature workbench from the recording verification.
