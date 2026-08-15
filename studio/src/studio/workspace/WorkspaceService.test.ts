// Pure tests for the semantic workspace controller. Uses the REAL FlexLayout
// Model (model operations are DOM-independent) — no rendering involved.

import { describe, expect, it, vi } from 'vitest'
import { WorkspaceService } from './WorkspaceService'
import { DEFAULT_WORKSPACE_TABSET_ID } from './defaultLayout'
import { workspaceViewInstanceId } from './workspaceTypes'
import { Actions, DockLocation } from 'flexlayout-react'

describe('WorkspaceService', () => {
  it('focuses the overview view by default', () => {
    const service = new WorkspaceService()
    expect(service.getFocusedViewKind()).toBe('overview')
  })

  it('focusView selects the view tab and notifies subscribers once', () => {
    const service = new WorkspaceService()
    const listener = vi.fn()
    service.subscribe(listener)

    service.focusView('chat')

    expect(service.getFocusedViewKind()).toBe('chat')
    expect(listener).toHaveBeenCalledTimes(1)
    expect(listener).toHaveBeenCalledWith('chat')
  })

  it('focusing the already-focused view fires no duplicate notification', () => {
    const service = new WorkspaceService()
    service.focusView('chat')
    const listener = vi.fn()
    service.subscribe(listener)

    service.focusView('chat')

    expect(listener).not.toHaveBeenCalled()
    expect(service.getFocusedViewKind()).toBe('chat')
  })

  it('openView behaves like focus in V1 (one tab per kind, always present)', () => {
    const service = new WorkspaceService()

    service.openView('knowledge')

    expect(service.getFocusedViewKind()).toBe('knowledge')
  })

  it('showSource and showDiff are semantic aliases for the source and git views', () => {
    const service = new WorkspaceService()

    service.showSource()
    expect(service.getFocusedViewKind()).toBe('source')

    service.showDiff()
    expect(service.getFocusedViewKind()).toBe('git')
  })

  it('focus follows the selected tab of the active tabset after a split', () => {
    const service = new WorkspaceService()
    const listener = vi.fn()
    service.subscribe(listener)

    // Drag the source tab to the right edge: new tabset, selected tab = source.
    service.getModel().doAction(Actions.moveNode(
      workspaceViewInstanceId('source'),
      DEFAULT_WORKSPACE_TABSET_ID,
      DockLocation.RIGHT,
      -1,
      true,
    ))
    expect(service.getFocusedViewKind()).toBe('source')

    // Focusing a view in the original tabset reactivates that tabset.
    service.focusView('git')
    expect(service.getFocusedViewKind()).toBe('git')

    expect(listener.mock.calls.map(call => call[0])).toEqual(['source', 'git'])
  })

  it('stops notifying after unsubscribe', () => {
    const service = new WorkspaceService()
    const listener = vi.fn()
    const unsubscribe = service.subscribe(listener)

    unsubscribe()
    service.focusView('chat')

    expect(listener).not.toHaveBeenCalled()
  })

  it('accepts a saved layout for future persistence restore', () => {
    const service = new WorkspaceService()
    service.getModel().doAction(Actions.moveNode(
      workspaceViewInstanceId('source'),
      DEFAULT_WORKSPACE_TABSET_ID,
      DockLocation.RIGHT,
      -1,
      true,
    ))
    const saved = service.getModel().toJson()

    const restored = new WorkspaceService(saved)

    let tabsetCount = 0
    restored.getModel().visitNodes(node => {
      if (node.getType() === 'tabset') tabsetCount += 1
    })
    expect(tabsetCount).toBe(2)
    expect(restored.getFocusedViewKind()).toBe('source')
  })
})
