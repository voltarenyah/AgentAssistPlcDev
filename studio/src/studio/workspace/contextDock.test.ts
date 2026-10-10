import { describe, expect, it } from 'vitest'
import { resolveContextDock, type ContextDockInputs } from './contextDock'
import type { WorkspaceViewKind } from './workspaceTypes'

const base: ContextDockInputs = {
  worktreeId: 'wt1',
  deviceId: 'dev1',
  mainViewKind: 'device',
  focusedView: 'overview',
  hasKnowledgeContext: true,
}

const resolve = (overrides: Partial<ContextDockInputs> = {}) =>
  resolveContextDock({ ...base, ...overrides })

const ALL_FOCUSES: Array<WorkspaceViewKind | null> = ['overview', 'chat', 'source', 'knowledge', 'inspector', null]

describe('resolveContextDock', () => {
  it('gives a selected device its properties panel on every focus', () => {
    // The rail exists on every surface now, so a device on a chat, source or inspector view keeps a
    // properties panel instead of resolving to no dock at all (ADR-0015).
    for (const focusedView of ['overview', 'chat', 'source', 'inspector', null] as Array<WorkspaceViewKind | null>) {
      expect(resolve({ focusedView }), `focus ${focusedView}`).toEqual({
        properties: 'device',
        defaultPage: 'properties',
      })
    }
  })

  it('prefers the knowledge panel on a knowledge focus that has a selection', () => {
    expect(resolve({ focusedView: 'knowledge' }))
      .toEqual({ properties: 'knowledge', defaultPage: 'properties' })
  })

  it('falls back to the device panel when the knowledge view has no selection', () => {
    expect(resolve({ focusedView: 'knowledge', hasKnowledgeContext: false }))
      .toEqual({ properties: 'device', defaultPage: 'properties' })
  })

  it('gives the worktree hardware target the hardware panel on every one of its pages', () => {
    // The configuration, BOM and network pages share one node selection, so the page describes that
    // selection on all three rather than leaving two of them with a hint that contradicts the screen.
    expect(resolve({ deviceId: null, mainViewKind: 'hardware' }))
      .toEqual({ properties: 'hardware', defaultPage: 'properties' })
    expect(resolve({ deviceId: null, mainViewKind: 'hardware', focusedView: 'chat' }))
      .toEqual({ properties: 'hardware', defaultPage: 'properties' })
  })

  it('has no properties panel on the worktree landing page and opens its working tree', () => {
    for (const focusedView of ALL_FOCUSES) {
      expect(resolve({ deviceId: null, mainViewKind: 'worktree', focusedView }), `focus ${focusedView}`)
        .toEqual({ properties: null, defaultPage: 'changes' })
    }
  })

  it('gives no selection at all the properties page and its empty state', () => {
    for (const focusedView of ALL_FOCUSES) {
      expect(resolve({ worktreeId: null, deviceId: null, mainViewKind: 'project', focusedView }), `focus ${focusedView}`)
        .toEqual({ properties: null, defaultPage: 'properties' })
    }
  })

  it('never resolves to an absent dock', () => {
    // Every combination of the matrix yields a page to open: the rail is always there and every page
    // has a named empty state.
    const worktrees = [null, 'wt1']
    const devices = [null, 'dev1']
    const views = ['project', 'worktree', 'hardware', 'device'] as const
    for (const worktreeId of worktrees) {
      for (const deviceId of devices) {
        for (const mainViewKind of views) {
          for (const focusedView of ALL_FOCUSES) {
            const state = resolveContextDock({
              worktreeId, deviceId, mainViewKind, focusedView, hasKnowledgeContext: true,
            })
            expect(state.defaultPage, `${worktreeId}/${deviceId}/${mainViewKind}/${focusedView}`)
              .toMatch(/^(properties|changes|history)$/)
          }
        }
      }
    }
  })
})
