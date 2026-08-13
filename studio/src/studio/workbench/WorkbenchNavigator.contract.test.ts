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

  it('exposes project, worktree, and device TIA access actions at their owning nodes', () => {
    expect(source).toContain('Open TIA with UI')
    expect(source).toContain('Open TIA with upgrade')
    expect(source).toContain('onOpenWorkbench')
    expect(source).toContain('onOpenWorktree')
    expect(source).toContain('onOpenDevice')
  })

  it('exposes archive from the worktree context menu', () => {
    expect(source).toContain('onArchiveWorktree')
    expect(source).toContain('Archive TIA project')
  })
})
