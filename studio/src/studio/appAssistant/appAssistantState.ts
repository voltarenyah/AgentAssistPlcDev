import type { AppAssistantEvent, AppAssistantRuntimeSnapshot } from '@/api/client'

export type AppAssistantMessage = {
  role: 'user' | 'assistant' | 'error'
  content: string
}

export type AppAssistantPanelState = {
  messages: AppAssistantMessage[]
  runtime: AppAssistantRuntimeSnapshot | null
  assistantRevision: number | null
  contextStale: boolean
  autoRefreshPending: boolean
  pendingApproval: Record<string, unknown> | null
  busy: boolean
}

export const initialAppAssistantState = (runtime: AppAssistantRuntimeSnapshot | null): AppAssistantPanelState => ({
  messages: [],
  runtime,
  assistantRevision: runtime?.workbenchRevision ?? null,
  contextStale: false,
  autoRefreshPending: false,
  pendingApproval: null,
  busy: false,
})

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
      const snapshot = event.data.runtimeSnapshot as AppAssistantRuntimeSnapshot
      const currentRuntime = next.runtime
      const runtime = !currentRuntime || snapshot.workbenchRevision >= currentRuntime.workbenchRevision
        ? snapshot
        : currentRuntime
      next = {
        ...next,
        runtime,
        assistantRevision: snapshot.workbenchRevision,
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
