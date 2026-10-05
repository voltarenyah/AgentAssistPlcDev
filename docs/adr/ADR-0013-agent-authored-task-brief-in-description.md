# ADR-0013 Agent-authored task briefs in the existing description

## Status

Accepted

## Context

A device conversation is where a PLC problem is usually found: the user asks the knowledge agent about
a block or a network, the agent grounds its answer in the offline knowledge database, and the
conversation establishes a defect, a risk, or a missing interlock. The user needs that finding recorded
as a task before the conversation is lost — with the background, the evidence behind it, and the
solutions that were considered — without retyping what the conversation already produced.

Only the Workbench Assistant could create a task (`assistant_create_task`); the device chat had no
task-creation tool at all. The device chat's new `create_task` tool closes that gap and additionally
carries the three brief artifacts the user named.

`GraphTask` persists `Title`, `Type`, `Status`, `Description`, and — inside `metadata_json` — `priority`,
`intent`, `expectedResult`, `elementRefs` and `doneUtc`. The task page renders `description` as
Markdown in the brief's "Details" section (`TaskBriefDisclosure`), and the Workbench Assistant already
writes a free-form `description` through the same creation path. ADR-0007 decided the comparable
task-field question the other way for a field that *gates* create validation, and recorded in its
persisted-representation table that `metadata_json` is "already a real payload home".

## Decision Point

- **Question**: how does the brief an agent establishes in a conversation (background, evidence,
  proposed solutions) persist on the task it creates?
- **Why a decision exists**: composing it into the existing `description` needs no contract change; a
  structured representation needs either new `metadata_json` payload fields (plus the API response, the
  client type and the task page) or new typed columns, and buys per-section addressing the current
  requirement does not ask for.
- **Scope boundary**: the persisted representation of an agent-authored task brief and how the task page
  reads it. It does not decide whether the device chat may create tasks (a user decision, implemented),
  and it does not change any gating task field.

## Decision

The brief is composed into the task's existing `description` as Markdown sections, in reading order:
`## Background`, `## Evidence`, `## Proposed solutions`. A section with no content is omitted, and a
call that established none of the three writes no description at all. No task field, table column,
API contract, or client type is added.

### Decision Details

| Item | Content |
|------|---------|
| **Decision** | An agent-authored brief is Markdown sections inside the task's existing `description`; empty sections are omitted. |
| **Why this** | The description is already a Markdown payload the task page renders, and the three artifacts are prose a reader consumes as a whole — not values anything queries, filters, or validates. |
| **Known unknowns** | Whether a later surface needs to address one section (for example, to require evidence before a task commit). |
| **Reconsider when** | A section must be validated, filtered, or mutated independently of the others, or a consumer must read the evidence as data rather than prose. |

## Rationale

### Options Considered

| Option | Requirement and repository fit | Current-scope benefit | Lifecycle cost | Maintainability | Material trade-offs |
|---|---|---|---|---|---|
| A. Compose into the existing `description` | The description is already Markdown the brief renders; ADR-0007 classifies non-gating payload as belonging in the payload home; the Workbench Assistant already writes a free-form description through the same creation path | No schema, API, client, or task-page change; the three artifacts arrive in one unit of work | The section headings become a convention that a later change must keep reading | One writer (`TaskCreationTool`), one reader (the task page's Markdown rendering); no new naming across layers | Sections cannot be queried, validated, or edited independently; a reader that wants "the evidence" must parse the text |
| B. Three new `metadata_json` payload fields | `metadata_json` already carries payload (`priority`, `intent`, `expectedResult`, `elementRefs`); needs the API response, the client type and the task page to expose them | Each section is addressable and independently renderable | Four files change for a payload nothing yet queries; two representations of the same prose (fields and rendered sections) can drift | Follows ADR-0007's payload-vs-column reasoning | An opaque bag grows with fields whose only consumer is one page; a partial write can leave a task whose evidence contradicts its text |
| C. Three typed columns | Mirrors how `target_kind` was added | Strongest typing and queryability | An additive migration, task contract, API, client and task-page change for prose | One representation per fact | Highest lifecycle cost for a requirement that asks only that the finding be recorded with its reasoning |

**Selected**: A. The requirement is to carry the reasoning with the task, and the description is already
the payload field a reader sees; B and C pay contract and migration costs for addressing a section that
nothing in the current scope addresses.

## Consequences

### Positive Consequences

- The device chat's tool, the Workbench Assistant's tool and the UI's create dialog keep writing the same
  field, so no new representation enters the task contract.
- A task created from a conversation reads exactly as the conversation established it, in one place.
- The findings survive in Git-tracked task data without a schema migration on existing workbenches.

### Negative Consequences

- The section headings are a convention, not a contract: nothing validates the shape of a hand-edited
  description afterwards.
- A future feature that needs structured evidence must parse or migrate the text.
- The brief is limited to prose; an agent cannot attach a machine-readable evidence record beyond the
  separately staged source objects (ADR-0003).

### Neutral Consequences

Staging source objects onto a task stays a separate, separately approved call. Creating a task from the
device chat therefore records prose evidence, and the task's compare basis is still established by
`stage_task_source_object`.

## Architecture Impact

`TaskCreationTool` composes the description; the graph write, the API contract and the task page are
unchanged. The only cross-cutting addition is the tool's destructive sandbox tier, which routes the call
through the existing `AgentSandbox` approval card.

## Implementation Guidance

Keep the composed sections in one place, with one fixed order, and omit empty ones — the task page must
never render a heading a reader would read as missing evidence. Add a section only when a requirement
needs it, and if a section ever has to be validated or queried, promote the whole brief to a structured
representation instead of parsing the text in a second place.

## Related Information

- `docs/adr/ADR-0003-task-scoped-source-evidence.md` — what a task's source evidence is
- `docs/adr/ADR-0007-task-target-model.md` — persisted-representation reasoning for a gating task field
- `src/ApiHost/TaskCreationTool.cs` — the composer and the tool's contract
- `src/ApiHost/TaskSourceStagingTool.cs` — the sibling device-chat tool and its approval shape
- `studio/src/studio/workbench/TaskBriefDisclosure.tsx` — the reader that renders the description
