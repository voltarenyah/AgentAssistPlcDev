# Design Document: TIA Compare Optional Hardware Verification

## Overview

- Outcome: retain full TIA Compare as the default while allowing a user to omit current hardware verification for a source-and-safety-only compare.
- Scope: Compare API request/result contract, Agent orchestration, and Version Control UI.
- UI Spec: `docs/ui-spec/tia-compare-optional-hardware-verification-ui-spec.md`.
- Governing ADRs: `docs/adr/ADR-0001-mandatory-commit-bound-software-evidence.md`.

## Requirement Boundary

- Convergence carrier: #89; user confirmed hardware verification is checked by default and safety remains included.
- Current requirements: default existing callers to full hardware/source/safety comparison; skip only hardware export when selected; expose explicit coverage; label a partial clean result accurately; disable hardware acceptance without a hardware staging result.
- Non-goals: safety opt-out, persisted preference, hardware cache, or a changed AML/network comparison algorithm.
- Open requirement fields: none.

## Acceptance Criteria

- **AC-001** — When a caller omits the new option or sends `includeHardware=true`, Compare shall preserve the existing hardware export and full-result behavior. Source: #89.
- **AC-002** — When a caller sends `includeHardware=false`, Compare shall not invoke `export_hardware_configuration`, but shall still run managed-source and F-signature comparison. Source: #89.
- **AC-003** — When a partial compare is otherwise clean, the Studio shall report managed-source and safety coverage and shall not claim `TIA matches master`. Source: #89.
- **AC-004** — When hardware was not checked, the response shall record that fact explicitly and the Studio shall not offer hardware acceptance. Source: #89.

## Existing Evidence

| Evidence | Location | Design effect |
|---|---|---|
| Compare unconditionally measures hardware before software evidence | `src/Agent/Workbench/WorkbenchConsistencyService.cs:126` | Guard only the hardware call behind the new default-on flag. |
| Overall state currently requires an in-sync hardware result | `src/Agent/Workbench/WorkbenchConsistencyService.cs:290` | Treat skipped hardware as outside the requested coverage while preserving source/safety verdicts. |
| Existing result already permits nullable hardware | `src/Agent/Workbench/ConsistencyModels.cs:87` | Add an explicit `HardwareChecked` flag so null is never overloaded. |
| Client/API currently use optional query parameters | `studio/src/api/client.ts:913`, `src/ApiHost/WorkbenchApiModels.cs:810` | Add additive `includeHardware=false` request handling with a true default. |
| Current clean label uses only hardware differences and safety changes | `studio/src/studio/version-control/VersionControlCompare.tsx:239` | Use coverage metadata to prevent a false full-match label. |

## Design

### Selected Design

Add `includeHardware` as an optional Compare request parameter defaulting to `true` at the API, coordinator, and consistency-service boundaries. When false, the service records a zero-duration `hardware-export` timing with an explicit skipped outcome and leaves the hardware result absent. It still performs every source and safety step.

Add `HardwareChecked` to the persisted comparison result. Existing serialized results without the field are interpreted as full coverage by the Studio. The consistency state continues to describe the checks requested by the caller; Studio uses `HardwareChecked` to distinguish full clean from partial clean. Hardware acceptance remains available only when an actual hardware comparison result is present.

The Version Control header owns a transient, checked-by-default checkbox and passes its selection through the existing Compare signal/props. It resets to checked when the panel selection changes and is not persisted.

### Change Surface

| Responsibility or expected file | Change | Governing source | Unaffected boundary to preserve |
|---|---|---|---|
| Consistency service/models | Optional hardware orchestration and explicit coverage | AC-001, AC-002, AC-004 | Source evidence, safety, untrackable, and hardware algorithms |
| Coordinator/API | Additive default-on query propagation | AC-001, AC-002 | Existing no-query API clients |
| Studio API/client | Typed optional request and coverage response | AC-001, AC-004 | Existing callers and result parsing |
| Version Control panel/compare | Transient checkbox and partial clean presentation | AC-003, AC-004 | Existing full clean and hardware-difference workflows |
| Focused tests | Request, orchestration, and visible partial state | All | Existing full-compare tests |

### Contracts, State, and Persistence

| Boundary | Input / exact format | Output / exact format | Error or state behavior | Compatibility |
|---|---|---|---|---|
| Studio → API | `POST .../compare-tia?includeHardware=false` | existing result | false skips only hardware verification | query omission means true |
| API → Agent | `includeHardware: bool = true` | existing result | source/safety errors remain unchanged | existing call sites compile unchanged |
| Agent → Studio | `hardwareChecked: boolean` plus nullable `hardware` | partial coverage visible | skipped is distinct from missing/failed evidence | old results default to checked in Studio |

## Implementation Approach

- Slicing: vertical.
- Dependency order: result/request contract and service guard; API/client propagation; panel state and result presentation; focused verification.
- First observable checkpoint: an Agent test proves `includeHardware=false` does not call hardware export while safety/source behavior remains present.
- Rationale: this is the smallest additive contract that preserves default full verification and gives partial runs an unambiguous result.

## Verification Strategy

| Claim / AC | Level | Repository command or operation | Observable pass condition |
|---|---|---|---|
| Hardware skip preserves source/safety compare | L1 | focused `WorkbenchConsistencyServiceTests` | no hardware tool call, explicit `HardwareChecked=false`, clean source/safety result |
| API default and false query propagation | L2 | focused `WorkbenchEndpointsTests` | omitted option remains true; false reaches the coordinator |
| Default UI and partial-result label | L1 | `VersionControlPanel` / `VersionControlCompare` Vitest tests | checkbox checked initially; partial card says hardware was not checked and has no accept control |
| Contract type/build | L1 | Studio build and Agent/API test projects | TypeScript and C# compile without widened ambiguity |
| User-visible behavior | L3 | local Studio compare with hardware unchecked | source/safety run and no hardware-export phase is invoked |

## Material Risks

| Risk | Evidence | In-scope response or verification |
|---|---|---|
| A null hardware result is treated as a full match | Existing UI checks only hardware difference | Explicit coverage flag plus partial clean test |
| Existing clients accidentally lose hardware verification | API has optional query parameters | `true` defaults through every layer and omitted-request tests |

## References

- GitHub issue #89
- `docs/design/tia-compare-fingerprint-first-design.md`

## Update History

| Date | Version | Changes |
|---|---|---|
| 2026-09-08 | 1.0 | Initial design for #89. |
