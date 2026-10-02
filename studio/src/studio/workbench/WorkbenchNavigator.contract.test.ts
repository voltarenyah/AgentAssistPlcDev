import { readFileSync } from 'node:fs'
import { describe, expect, it } from 'vitest'

const source = readFileSync(new URL('./WorkbenchNavigator.tsx', import.meta.url), 'utf8')

describe('hardware worktree tree item', () => {
  // The hardware row's position relative to the PLC device list is asserted against the rendered
  // DOM in WorkbenchNavigator.test.tsx. A source-text index cannot state it: it broke when the
  // device list stopped rendering through a local named `devices` while the order was unchanged.
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
