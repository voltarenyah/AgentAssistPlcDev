// Persistence tests for the workspace layout envelope. Uses the real
// FlexLayout Model (DOM-independent) and a Map-backed Storage stub.

import { describe, expect, it, vi } from 'vitest'
import { Actions, DockLocation, Model, type IJsonModel } from 'flexlayout-react'
import {
  readWorkspaceLayout,
  WORKSPACE_LAYOUT_STORAGE_KEY,
  writeWorkspaceLayout,
} from './workspaceLayoutStorage'
import { WorkspaceService } from './WorkspaceService'
import { buildDefaultWorkspaceLayout, DEFAULT_WORKSPACE_TABSET_ID } from './defaultLayout'
import { DEFAULT_WORKSPACE_VIEW_KINDS, workspaceViewInstanceId } from './workspaceTypes'

const memoryStorage = (): Storage => {
  const entries = new Map<string, string>()
  return {
    getItem: (key: string) => entries.get(key) ?? null,
    setItem: (key: string, value: string) => void entries.set(key, value),
    removeItem: (key: string) => void entries.delete(key),
    clear: () => entries.clear(),
    key: () => null,
    get length() { return entries.size },
  } as Storage
}

const splitSourceIntoSecondTabset = (model: Model) => {
  model.doAction(Actions.moveNode(
    workspaceViewInstanceId('source'),
    DEFAULT_WORKSPACE_TABSET_ID,
    DockLocation.RIGHT,
    -1,
    true,
  ))
}

const tabsetCount = (model: Model): number => {
  let count = 0
  model.visitNodes(node => {
    if (node.getType() === 'tabset') count += 1
  })
  return count
}

describe('workspaceLayoutStorage', () => {
  it('round-trips a split layout through storage into a restored service', () => {
    const storage = memoryStorage()
    const model = Model.fromJson(buildDefaultWorkspaceLayout())
    splitSourceIntoSecondTabset(model)
    writeWorkspaceLayout(storage, model.toJson())

    const restored = readWorkspaceLayout(storage)
    expect(restored).not.toBeNull()

    const service = new WorkspaceService(restored)
    expect(tabsetCount(service.getModel())).toBe(2)
    expect(service.getFocusedViewKind()).toBe('source')
  })

  it('returns null for missing storage, missing key, and corrupt json', () => {
    expect(readWorkspaceLayout(null)).toBeNull()
    expect(readWorkspaceLayout(memoryStorage())).toBeNull()

    const corrupt = memoryStorage()
    corrupt.setItem(WORKSPACE_LAYOUT_STORAGE_KEY, '{not json')
    expect(readWorkspaceLayout(corrupt)).toBeNull()
  })

  it('returns null for a wrong envelope version', () => {
    const storage = memoryStorage()
    storage.setItem(WORKSPACE_LAYOUT_STORAGE_KEY, JSON.stringify({
      version: 2,
      layout: buildDefaultWorkspaceLayout(),
    }))
    expect(readWorkspaceLayout(storage)).toBeNull()
  })

  it('returns null when any tab id is not a known view instance', () => {
    const storage = memoryStorage()
    const layout = buildDefaultWorkspaceLayout()
    layout.layout.children[0].children?.push({ type: 'tab', id: 'view:retired', name: 'Retired', component: 'retired' })
    storage.setItem(WORKSPACE_LAYOUT_STORAGE_KEY, JSON.stringify({ version: 1, layout }))
    expect(readWorkspaceLayout(storage)).toBeNull()
  })

  it('returns null for a layout without the FlexLayout shape', () => {
    const storage = memoryStorage()
    storage.setItem(WORKSPACE_LAYOUT_STORAGE_KEY, JSON.stringify({ version: 1, layout: { nope: true } }))
    expect(readWorkspaceLayout(storage)).toBeNull()
  })

  it('never throws on a throwing Storage stub', () => {
    const throwing = {
      getItem: () => { throw new Error('denied') },
      setItem: () => { throw new Error('quota') },
    } as unknown as Storage
    expect(() => writeWorkspaceLayout(throwing, buildDefaultWorkspaceLayout())).not.toThrow()
    expect(readWorkspaceLayout(throwing)).toBeNull()
    expect(() => writeWorkspaceLayout(null, buildDefaultWorkspaceLayout())).not.toThrow()
  })
})

describe('WorkspaceService persistence hooks', () => {
  it('reports layout changes after navigation and geometry actions, not on construction', () => {
    const writes: IJsonModel[] = []
    const service = new WorkspaceService(null, layout => writes.push(layout))
    expect(writes).toHaveLength(0)

    service.focusView('chat')
    expect(writes).toHaveLength(1)

    splitSourceIntoSecondTabset(service.getModel())
    expect(writes.length).toBeGreaterThanOrEqual(2)

    // The last write captures the split geometry and round-trips.
    const restored = new WorkspaceService(readWorkspaceLayout((() => {
      const storage = memoryStorage()
      writeWorkspaceLayout(storage, writes[writes.length - 1])
      return storage
    })()))
    expect(tabsetCount(restored.getModel())).toBe(2)
  })

  it('resetLayout restores the default five-tab tabset and notifies subscribers', () => {
    const writes: IJsonModel[] = []
    const service = new WorkspaceService(null, layout => writes.push(layout))
    const modelListener = vi.fn()
    service.subscribeModel(modelListener)
    splitSourceIntoSecondTabset(service.getModel())
    expect(tabsetCount(service.getModel())).toBe(2)
    const versionBefore = service.getModelSnapshot().version

    service.resetLayout()

    expect(service.getModelSnapshot().version).toBe(versionBefore + 1)
    expect(modelListener).toHaveBeenCalledTimes(1)
    expect(service.getFocusedViewKind()).toBe('overview')
    expect(tabsetCount(service.getModel())).toBe(1)

    // The reset reports default geometry, which overwrites any persisted split.
    const last = writes[writes.length - 1]
    const tabs = last.layout.children[0].children ?? []
    expect(tabs.map(tab => tab.id)).toEqual(DEFAULT_WORKSPACE_VIEW_KINDS.map(workspaceViewInstanceId))
  })
})
