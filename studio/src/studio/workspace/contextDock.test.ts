import { describe, expect, it } from 'vitest'
import { resolveContextDock, type ContextDockInputs } from './contextDock'
import type { WorkspaceViewKind } from './workspaceTypes'

const base: ContextDockInputs = {
  worktreeId: 'wt1',
  deviceId: 'dev1',
  mainViewKind: 'device',
  hardwarePage: null,
  focusedView: 'overview',
  hasKnowledgeContext: true,
}

const resolve = (overrides: Partial<ContextDockInputs> = {}) =>
  resolveContextDock({ ...base, ...overrides })

describe('resolveContextDock', () => {
  it('hides the dock without a worktree (project landing)', () => {
    for (const focusedView of ['overview', 'chat', 'source', 'knowledge'] as WorkspaceViewKind[]) {
      const state = resolve({ worktreeId: null, deviceId: null, mainViewKind: 'project', focusedView })
      expect(state.visible, `focus ${focusedView}`).toBe(false)
      expect(state.content.kind).toBe('none')
    }
  })

  it('shows the version-control dock on the worktree landing page regardless of stale focus', () => {
    // Version control is worktree-level: the dock hosts it whenever the
    // worktree landing page is shown, even with a stale device-view focus.
    for (const focusedView of ['overview', 'chat', 'source', 'knowledge', null] as Array<WorkspaceViewKind | null>) {
      const state = resolve({ deviceId: null, mainViewKind: 'worktree', focusedView })
      expect(state, `focus ${focusedView}`).toEqual({ visible: true, content: { kind: 'version-control' } })
    }
  })

  it('shows the hardware dock on the hardware tree page without a device', () => {
    const state = resolve({ deviceId: null, mainViewKind: 'hardware', hardwarePage: 'tree', focusedView: 'overview' })
    expect(state).toEqual({ visible: true, content: { kind: 'hardware' } })
  })

  it('keeps device/session docks out of hardware pages even with stale focus', () => {
    // A stale workspace focus left over from before the hardware page opened
    // must not stack a device dock on top of the hardware dock.
    const stale = resolve({ deviceId: null, mainViewKind: 'hardware', hardwarePage: 'tree', focusedView: 'chat' })
    expect(stale).toEqual({ visible: true, content: { kind: 'hardware' } })

    for (const page of ['bom', 'network'] as const) {
      const state = resolve({ deviceId: null, mainViewKind: 'hardware', hardwarePage: page, focusedView: 'overview' })
      expect(state).toEqual({ visible: true, content: { kind: 'none' } })
    }
  })

  it('shows the device dock for a device with overview focus', () => {
    expect(resolve()).toEqual({ visible: true, content: { kind: 'device' } })
  })

  it('shows the knowledge dock for knowledge focus only when the context exists', () => {
    expect(resolve({ focusedView: 'knowledge' }))
      .toEqual({ visible: true, content: { kind: 'knowledge' } })
    expect(resolve({ focusedView: 'knowledge', hasKnowledgeContext: false }))
      .toEqual({ visible: true, content: { kind: 'none' } })
  })

  it('shows the session dock for chat and source focus', () => {
    expect(resolve({ focusedView: 'chat' }))
      .toEqual({ visible: true, content: { kind: 'sessions' } })
    expect(resolve({ focusedView: 'source' }))
      .toEqual({ visible: true, content: { kind: 'sessions' } })
  })

  it('falls back to the session dock for a null focus with a device (current behavior)', () => {
    expect(resolve({ focusedView: null }))
      .toEqual({ visible: true, content: { kind: 'sessions' } })
  })
})
