import { readFileSync } from 'node:fs'
import { describe, expect, it } from 'vitest'

const source = readFileSync(new URL('./WorkbenchNavigator.tsx', import.meta.url), 'utf8')

describe('hardware worktree tree item', () => {
  it('renders hardware configuration before the PLC device list', () => {
    const hardwareIndex = source.indexOf('Hardware configuration')
    const devicesIndex = source.indexOf('devices.map')

    expect(hardwareIndex).toBeGreaterThanOrEqual(0)
    expect(devicesIndex).toBeGreaterThan(hardwareIndex)
  })

  it('exposes reload and compare actions from the hardware context menu', () => {
    expect(source).toContain('Reload hardware configuration')
    expect(source).toContain('Compare hardware with TIA')
  })
})
