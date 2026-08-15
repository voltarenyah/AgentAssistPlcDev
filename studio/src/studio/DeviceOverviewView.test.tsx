// @vitest-environment happy-dom
import React, { act } from 'react'
import { createRoot } from 'react-dom/client'
import { afterEach, describe, expect, it, vi } from 'vitest'
import * as api from '@/api/client'
import DeviceOverviewView, { type DeviceOverviewViewProps } from './DeviceOverviewView'

globalThis.IS_REACT_ACT_ENVIRONMENT = true

const info = {
  deviceId: 'dev1',
  plcName: 'PLC_1',
  engineeringIdentity: 'eng-1',
  sourceRoot: 'C:/wb/source',
  knowledgeDbPath: 'C:/wb/plc-knowledge.db',
  sourceProjectPath: 'C:/tia/TestPLCExportDemo.ap17',
} as api.DeviceInfo

const meta = {
  plcName: 'PLC_1',
  deviceName: 'CPU 1516',
  typeIdentifier: 'OrderNumber:6ES7 516-3AN02-0AB0',
} as api.DeviceExportMetadata

const makeProps = (overrides: Partial<DeviceOverviewViewProps> = {}): DeviceOverviewViewProps => ({
  deviceName: 'PLC_1',
  deviceId: 'dev1',
  deviceInfo: info,
  deviceMeta: meta,
  deviceView: null,
  blocks: [],
  displayedSourceObjectCount: 7,
  deviceSessions: [],
  activeKnowledge: 'current',
  isBrandNewDevice: false,
  matchingTiaSession: null,
  operation: null,
  rebuildArmed: false,
  setRebuildArmed: vi.fn(),
  activeWorktree: null,
  onOpenProjectInTia: vi.fn(),
  onAttachTiaInstance: vi.fn(),
  onStageRefresh: vi.fn(),
  onRebuildProject: vi.fn(),
  onUpdateKnowledge: vi.fn(),
  onMergeIntoMaster: vi.fn(),
  onBootstrapDevice: vi.fn(),
  ...overrides,
})

const render = async (element: React.ReactNode) => {
  const host = document.createElement('div')
  document.body.appendChild(host)
  const root = createRoot(host)
  await act(async () => root.render(element))
  return { host, root }
}

const clickButton = async (host: HTMLElement, text: string) => {
  const button = Array.from(host.querySelectorAll('button'))
    .find(element => element.textContent?.includes(text))
  expect(button, `button containing "${text}"`).toBeTruthy()
  await act(async () => {
    button!.dispatchEvent(new MouseEvent('click', { bubbles: true }))
  })
  return button!
}

afterEach(() => {
  document.body.innerHTML = ''
})

describe('DeviceOverviewView', () => {
  it('renders the device identity and metric grid', async () => {
    const { host } = await render(<DeviceOverviewView {...makeProps()} />)

    expect(host.querySelector('h1')?.textContent).toBe('PLC_1')
    expect(host.textContent).toContain('eng-1')
    expect(host.textContent).toContain('6ES7 516-3AN02-0AB0')
    expect(host.textContent).toContain('CPU 1516')

    const sourceObjectsMetric = Array.from(host.querySelectorAll('div'))
      .find(element => element.textContent === 'Source objects')
    expect(sourceObjectsMetric?.previousElementSibling?.textContent).toBe('7')
  })

  it('arms the rebuild action before invoking it', async () => {
    const props = makeProps()
    const { host, root } = await render(<DeviceOverviewView {...props} />)

    await clickButton(host, 'Rebuild project')
    expect(props.setRebuildArmed).toHaveBeenCalledWith(true)
    expect(props.onRebuildProject).not.toHaveBeenCalled()

    const armed = { ...props, rebuildArmed: true }
    await act(async () => root.render(<DeviceOverviewView {...armed} />))
    expect(host.textContent).toContain('Confirm full rebuild?')
    await clickButton(host, 'Confirm full rebuild?')
    expect(armed.setRebuildArmed).toHaveBeenCalledWith(false)
    expect(armed.onRebuildProject).toHaveBeenCalledTimes(1)
  })

  it('shows the bootstrap panel instead of rebuild for a brand-new device', async () => {
    const props = makeProps({ isBrandNewDevice: true, activeKnowledge: 'missing' })
    const { host } = await render(<DeviceOverviewView {...props} />)

    expect(host.textContent).toContain('Start by generating the PLC context')
    expect(host.textContent).not.toContain('Rebuild project')

    await clickButton(host, 'Generate PLC context')
    expect(props.onBootstrapDevice).toHaveBeenCalledTimes(1)
  })

  it('offers re-attach and merge actions only when applicable', async () => {
    const tiaSession = { id: 4212 } as api.SessionInfo
    const worktree = { worktreeId: 'wt1', branch: 'feature/demo' } as api.WorkbenchRegistration
    const props = makeProps({ matchingTiaSession: tiaSession, activeWorktree: worktree })
    const { host } = await render(<DeviceOverviewView {...props} />)

    expect(host.textContent).toContain('Re-attach TIA instance (PID 4212)')
    await clickButton(host, 'Re-attach TIA instance')
    expect(props.onAttachTiaInstance).toHaveBeenCalledWith(4212)

    await clickButton(host, 'Merge to master')
    expect(props.onMergeIntoMaster).toHaveBeenCalledTimes(1)
  })

  it('hides merge on master and disables actions while an operation runs', async () => {
    const masterWorktree = { worktreeId: 'wt1', branch: 'master' } as api.WorkbenchRegistration
    const { host } = await render(<DeviceOverviewView
      {...makeProps({ activeWorktree: masterWorktree, operation: 'refresh' })}
    />)

    expect(host.textContent).not.toContain('Merge to master')
    const buttons = Array.from(host.querySelectorAll('button'))
    expect(buttons.length).toBeGreaterThan(0)
    for (const button of buttons) {
      expect(button.disabled).toBe(true)
    }
  })
})
