import { describe, expect, it } from 'vitest'
import {
  DEFAULT_SHELL_LAYOUT,
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

describe('shell layout', () => {
  it('uses open docks and reference widths by default', () => {
    expect(readShellLayout(null)).toEqual(DEFAULT_SHELL_LAYOUT)
  })

  it('clamps left and right widths to their safe ranges', () => {
    expect(clampDockWidth('left', 100)).toBe(240)
    expect(clampDockWidth('left', 999)).toBe(420)
    expect(clampDockWidth('right', 100)).toBe(240)
    expect(clampDockWidth('right', 999)).toBe(420)
  })

  it('round-trips a valid layout', () => {
    const storage = createStorage()
    const value = { version: 1 as const, leftOpen: false, rightOpen: true, leftWidth: 280, rightWidth: 360 }
    writeShellLayout(storage, value)
    expect(readShellLayout(storage)).toEqual(value)
  })

  it('falls back safely for malformed or unknown values', () => {
    const storage = { getItem: () => '{"version":99,"leftOpen":"yes"}' } as Storage
    expect(readShellLayout(storage)).toEqual(DEFAULT_SHELL_LAYOUT)
  })
})
