import { describe, expect, it } from 'vitest'
import {
  applyAssistantRuntimeSnapshot,
  initialAppAssistantState,
  type AppAssistantPanelState,
} from './appAssistantState'
import type { AppAssistantRuntimeSnapshot } from '@/api/client'

const snapshot = (overrides: Partial<AppAssistantRuntimeSnapshot> = {}): AppAssistantRuntimeSnapshot => ({
  schemaVersion: 1,
  workbenchId: 'wb1',
  workbenchRevision: 3,
  focus: { worktreeId: 'wt1', deviceId: null },
  worktrees: [{ worktreeId: 'wt1', name: 'master', branch: 'master', todoCount: 2, gitStatus: 'clean' }],
  availableActions: [],
  operation: { status: 'idle', operationId: null, kind: null, message: null },
  observedAt: '2026-08-09T00:00:00Z',
  ...overrides,
})

describe('app assistant runtime state', () => {
  it('marks focus-only revisions stale without scheduling a new suggestion', () => {
    const state = initialAppAssistantState(snapshot())
    const next = applyAssistantRuntimeSnapshot(state, snapshot({
      workbenchRevision: 4,
      focus: { worktreeId: 'wt2', deviceId: null },
    }), state.runtime)

    expect(next.contextStale).toBe(true)
    expect(next.autoRefreshPending).toBe(false)
    expect(next.runtime?.focus.worktreeId).toBe('wt2')
  })

  it('schedules a refresh when a worktree changes', () => {
    const state = initialAppAssistantState(snapshot())
    const next = applyAssistantRuntimeSnapshot(state, snapshot({
      workbenchRevision: 4,
      worktrees: [{ worktreeId: 'wt2', name: 'feature', branch: 'feature', todoCount: 0, gitStatus: 'dirty' }],
    }), state.runtime)

    expect(next.contextStale).toBe(true)
    expect(next.autoRefreshPending).toBe(true)
  })

  it('does not schedule an automatic refresh while a user request is running', () => {
    const state: AppAssistantPanelState = {
      ...initialAppAssistantState(snapshot()),
      busy: true,
    }
    const next = applyAssistantRuntimeSnapshot(state, snapshot({ workbenchRevision: 4, worktrees: [] }), state.runtime)

    expect(next.contextStale).toBe(true)
    expect(next.autoRefreshPending).toBe(false)
  })

  it('recognizes numeric completed operation statuses from ApiHost JSON', () => {
    const state = initialAppAssistantState(snapshot())
    const next = applyAssistantRuntimeSnapshot(state, snapshot({
      workbenchRevision: 4,
      operation: { operationId: 'op-1', kind: 'create-worktree', status: 3, message: 'done' },
    }), state.runtime)

    expect(next.autoRefreshPending).toBe(true)
  })
})
