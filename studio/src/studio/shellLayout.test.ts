import { describe, expect, it } from 'vitest'
import {
  DEFAULT_SHELL_LAYOUT,
  RIGHT_DOCK_RAIL_WIDTH,
  SHELL_LAYOUT_STORAGE_KEY,
  clampDockWidth,
  readShellLayout,
  writeShellLayout,
} from './shellLayout'

const createStorage = (): Storage => {
  const values = new Map<string, string>()
  return {
    getItem: key => values.get(key) ?? null,
    setItem: (key, value) => { values.set(key, value) },
    removeItem: key => { values.delete(key) },
    clear: () => { values.clear() },
    key: index => [...values.keys()][index] ?? null,
    get length() { return values.size },
  }
}

const v2 = {
  version: 2 as const,
  leftOpen: false,
  leftWidth: 280,
  rightColumnOpen: true,
  rightPanelWidth: 300,
  rightPanel: 'history' as const,
  rightPanelCollapsed: false,
}

describe('shell layout', () => {
  it('uses open docks and reference widths by default', () => {
    expect(readShellLayout(null)).toEqual(DEFAULT_SHELL_LAYOUT)
  })

  it('defaults to the right column width the shell had before the rail existed', () => {
    expect(DEFAULT_SHELL_LAYOUT.rightPanelWidth + RIGHT_DOCK_RAIL_WIDTH).toBe(310)
    expect(DEFAULT_SHELL_LAYOUT.rightPanel).toBeNull()
    expect(DEFAULT_SHELL_LAYOUT.rightPanelCollapsed).toBe(false)
  })

  it('clamps each side to its own safe range', () => {
    expect(clampDockWidth('left', 100)).toBe(240)
    expect(clampDockWidth('left', 999)).toBe(420)
    // The right column's page keeps the version-control toolbar on one row, so its floor is the
    // default page width itself — which is also what a migrated v1 layout lands on.
    expect(clampDockWidth('right', 100)).toBe(266)
    expect(clampDockWidth('right', 999)).toBe(420)
  })

  it('round-trips a valid layout, including a collapsed page', () => {
    const storage = createStorage()
    writeShellLayout(storage, v2)
    expect(readShellLayout(storage)).toEqual(v2)

    // Collapsing keeps the page, which is what lets the rail keep marking it.
    const collapsed = { ...v2, rightPanelCollapsed: true }
    writeShellLayout(storage, collapsed)
    expect(readShellLayout(storage)).toEqual(collapsed)
  })

  it('normalizes an absent page preference and unclamped widths', () => {
    const storage = createStorage()
    storage.setItem(SHELL_LAYOUT_STORAGE_KEY, JSON.stringify({
      version: 2,
      leftOpen: true,
      leftWidth: 9000,
      rightColumnOpen: false,
      rightPanelWidth: 10,
      rightPanelCollapsed: true,
    }))
    expect(readShellLayout(storage)).toEqual({
      version: 2,
      leftOpen: true,
      leftWidth: 420,
      rightColumnOpen: false,
      rightPanelWidth: 266,
      rightPanel: null,
      rightPanelCollapsed: true,
    })
  })

  it('migrates a v1 layout without moving the column or the workspace', () => {
    const storage = createStorage()
    storage.setItem('plc-studio.shell-layout.v1', JSON.stringify({
      version: 1, leftOpen: true, rightOpen: true, leftWidth: 310, rightWidth: 310,
    }))
    const migrated = readShellLayout(storage)
    expect(migrated).toEqual({
      version: 2,
      leftOpen: true,
      leftWidth: 310,
      rightColumnOpen: true,
      rightPanelWidth: 310 - RIGHT_DOCK_RAIL_WIDTH,
      rightPanel: null,
      rightPanelCollapsed: false,
    })
    // The page plus the rail is the width the v1 dock occupied.
    expect(migrated.rightPanelWidth + RIGHT_DOCK_RAIL_WIDTH).toBe(310)
  })

  it('keeps a migrated v1 right dock hidden when it was hidden', () => {
    const storage = createStorage()
    storage.setItem('plc-studio.shell-layout.v1', JSON.stringify({
      version: 1, leftOpen: false, rightOpen: false, leftWidth: 260, rightWidth: 420,
    }))
    expect(readShellLayout(storage)).toMatchObject({
      leftOpen: false, rightColumnOpen: false, leftWidth: 260, rightPanelWidth: 376,
    })
  })

  it('prefers a stored v2 layout over a leftover v1 one', () => {
    const storage = createStorage()
    storage.setItem('plc-studio.shell-layout.v1', JSON.stringify({
      version: 1, leftOpen: true, rightOpen: true, leftWidth: 310, rightWidth: 310,
    }))
    writeShellLayout(storage, v2)
    expect(readShellLayout(storage)).toEqual(v2)
  })

  it('falls back safely for malformed or unknown values', () => {
    const storage = { getItem: () => '{"version":99,"leftOpen":"yes"}' } as Storage
    expect(readShellLayout(storage)).toEqual(DEFAULT_SHELL_LAYOUT)
  })

  it('falls back safely for a malformed legacy payload', () => {
    const storage = { getItem: key => key.endsWith('.v1') ? '{"version":1}' : null } as Storage
    expect(readShellLayout(storage)).toEqual(DEFAULT_SHELL_LAYOUT)
  })

  it('falls back safely for an unknown page or a broken payload', () => {
    const storage = createStorage()
    storage.setItem(SHELL_LAYOUT_STORAGE_KEY, JSON.stringify({ ...v2, rightPanel: 'timeline' }))
    expect(readShellLayout(storage)).toEqual(DEFAULT_SHELL_LAYOUT)

    const broken = { getItem: () => '{oops', setItem: () => {}, } as unknown as Storage
    expect(readShellLayout(broken)).toEqual(DEFAULT_SHELL_LAYOUT)
  })
})
