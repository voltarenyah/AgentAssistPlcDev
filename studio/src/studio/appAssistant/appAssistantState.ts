import type { AppAssistantEvent, AppAssistantRuntimeSnapshot } from '@/api/client'

export type AppAssistantMessage = {
  role: 'user' | 'assistant' | 'error'
  content: string
}

export type AppAssistantClarificationOption = {
  value: string
  label: string
  description?: string | null
}

export type AppAssistantPanelState = {
  messages: AppAssistantMessage[]
  runtime: AppAssistantRuntimeSnapshot | null
  lastRunId: string | null
  feedbackSubmitted: boolean
  assistantRevision: number | null
  contextStale: boolean
  autoRefreshPending: boolean
  pendingApproval: Record<string, unknown> | null
  clarificationOptions: AppAssistantClarificationOption[]
  busy: boolean
}

export const initialAppAssistantState = (runtime: AppAssistantRuntimeSnapshot | null): AppAssistantPanelState => ({
  messages: [],
  runtime,
  lastRunId: null,
  feedbackSubmitted: false,
  assistantRevision: runtime?.workbenchRevision ?? null,
  contextStale: false,
  autoRefreshPending: false,
  pendingApproval: null,
  clarificationOptions: [],
  busy: false,
})

export const normalizeAssistantRuntimeSnapshot = (value: unknown): AppAssistantRuntimeSnapshot | null => {
  if (!value || typeof value !== 'object') return null
  const envelope = value as Record<string, unknown>
  const nested = envelope.runtime && typeof envelope.runtime === 'object'
    ? envelope.runtime as Record<string, unknown>
    : envelope
  const workbenchId = typeof nested.workbenchId === 'string'
    ? nested.workbenchId
    : typeof envelope.workbenchId === 'string' ? envelope.workbenchId : null
  if (!workbenchId) return null

  const focus = nested.focus && typeof nested.focus === 'object' ? nested.focus as Record<string, unknown> : {}
  const operation = nested.operation && typeof nested.operation === 'object' ? nested.operation as Record<string, unknown> : {}
  return {
    ...(nested as Partial<AppAssistantRuntimeSnapshot>),
    schemaVersion: typeof nested.schemaVersion === 'number' ? nested.schemaVersion : 1,
    workbenchId,
    workbenchRevision: typeof nested.workbenchRevision === 'number'
      ? nested.workbenchRevision
      : typeof envelope.contextRevision === 'number' ? envelope.contextRevision : 0,
    focus: {
      worktreeId: typeof focus.worktreeId === 'string' ? focus.worktreeId : null,
      deviceId: typeof focus.deviceId === 'string' ? focus.deviceId : null,
    },
    worktrees: Array.isArray(nested.worktrees) ? nested.worktrees as AppAssistantRuntimeSnapshot['worktrees'] : [],
    availableActions: Array.isArray(nested.availableActions)
      ? nested.availableActions as AppAssistantRuntimeSnapshot['availableActions']
      : Array.isArray(envelope.availableActions) ? envelope.availableActions as AppAssistantRuntimeSnapshot['availableActions'] : [],
    operation: {
      operationId: typeof operation.operationId === 'string' ? operation.operationId : null,
      kind: typeof operation.kind === 'string' ? operation.kind : null,
      status: typeof operation.status === 'string' || typeof operation.status === 'number' ? operation.status : 'idle',
      message: typeof operation.message === 'string' ? operation.message : null,
    },
    observedAt: typeof nested.observedAt === 'string'
      ? nested.observedAt
      : typeof envelope.observedAt === 'string' ? envelope.observedAt : new Date(0).toISOString(),
  }
}

const worktreeProjection = (runtime: AppAssistantRuntimeSnapshot) => runtime.worktrees.map(worktree => ({
  worktreeId: worktree.worktreeId,
  name: worktree.name,
  branch: worktree.branch,
  todoCount: worktree.todoCount,
  gitStatus: worktree.gitStatus,
}))

const operationStatusName = (status: string | number): string => {
  if (typeof status === 'number') return ['idle', 'running', 'awaitingapproval', 'succeeded', 'failed', 'cancelled'][status] ?? String(status)
  return status.toLowerCase()
}

export const isConsequentialRuntimeChange = (
  previous: AppAssistantRuntimeSnapshot | null,
  next: AppAssistantRuntimeSnapshot,
): boolean => {
  if (!previous) return false
  if (previous.focus.worktreeId !== next.focus.worktreeId
    || previous.focus.deviceId !== next.focus.deviceId) return true
  if (JSON.stringify(worktreeProjection(previous)) !== JSON.stringify(worktreeProjection(next))) return true
  const completedStatuses = new Set(['succeeded', 'failed', 'cancelled'])
  return previous.operation.operationId !== next.operation.operationId
    && next.operation.kind !== null
    && completedStatuses.has(operationStatusName(next.operation.status))
}

export const applyAssistantRuntimeSnapshot = (
  state: AppAssistantPanelState,
  snapshot: AppAssistantRuntimeSnapshot,
  previous: AppAssistantRuntimeSnapshot | null,
): AppAssistantPanelState => {
  const revisionChanged = state.assistantRevision !== null
    && snapshot.workbenchRevision > state.assistantRevision
  const consequential = revisionChanged && isConsequentialRuntimeChange(previous, snapshot)
  return {
    ...state,
    runtime: snapshot,
    contextStale: state.contextStale || revisionChanged,
    autoRefreshPending: state.autoRefreshPending || (consequential && !state.busy),
  }
}

export const applyAssistantEvents = (
  state: AppAssistantPanelState,
  events: AppAssistantEvent[],
): AppAssistantPanelState => {
  let next = state
  for (const event of events) {
    if (event.kind === 'answer' && typeof event.data.answer === 'string') {
      next = { ...next, messages: [...next.messages, { role: 'assistant', content: event.data.answer }] }
    } else if (event.kind === 'state' && event.data.runtimeSnapshot) {
      const snapshot = normalizeAssistantRuntimeSnapshot(event.data.runtimeSnapshot)
      if (!snapshot) continue
      const decision = event.data.decision as { options?: unknown } | undefined
      const clarificationOptions = Array.isArray(decision?.options)
        ? decision.options.filter((option): option is AppAssistantClarificationOption => {
          if (!option || typeof option !== 'object') return false
          const value = option as Record<string, unknown>
          return typeof value.value === 'string' && typeof value.label === 'string'
        })
        : []
      const currentRuntime = next.runtime
      const runtime = !currentRuntime || snapshot.workbenchRevision >= currentRuntime.workbenchRevision
        ? snapshot
        : currentRuntime
      next = {
        ...next,
        runtime,
        assistantRevision: snapshot.workbenchRevision,
        lastRunId: typeof (event.data.runMetadata as { runId?: unknown } | undefined)?.runId === 'string'
          ? (event.data.runMetadata as { runId: string }).runId
          : next.lastRunId,
        feedbackSubmitted: false,
        clarificationOptions,
        contextStale: runtime.workbenchRevision > snapshot.workbenchRevision,
        autoRefreshPending: false,
      }
    } else if (event.kind === 'error') {
      next = { ...next, messages: [...next.messages, { role: 'error', content: String(event.data.message ?? event.data.error ?? 'Assistant unavailable') }] }
    } else if (event.kind === 'interrupt' || event.kind === 'runtime-state') {
      next = { ...next, pendingApproval: event.data as Record<string, unknown> }
    }
  }
  return next
}
