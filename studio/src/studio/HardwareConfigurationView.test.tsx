// @vitest-environment happy-dom
import React, { act } from 'react'
import { createRoot } from 'react-dom/client'
import { afterEach, describe, expect, it, vi } from 'vitest'
import * as api from '@/api/client'
import HardwareConfigurationView from './HardwareConfigurationView'

globalThis.IS_REACT_ACT_ENVIRONMENT = true

const view: api.HardwareConfigurationView = {
  state: 'available',
  projectAmlPath: 'C:/worktree/hardware/Hardware/project.aml',
  exportedAt: '2026-08-04T00:00:00Z',
  message: null,
  tags: [{
    id: 'tag-1',
    name: 'DI_1_Tag',
    dataType: 'Bool',
    ioType: 'Input',
    logicalAddress: '460.3',
    ownerPath: 'PLC_1',
  }],
  devices: [{
    id: 'plc-1',
    name: 'PLC_1',
    path: 'PLC_1',
    kind: 'device',
    typeIdentifier: 'AutomationML/PLC',
    properties: [{ name: 'Order number', value: '6ES7-PLC' }],
    ioRanges: [],
    children: [{
      id: 'rack-1',
      name: 'Rack_0',
      path: 'PLC_1.Rack_0',
      kind: 'module',
      typeIdentifier: 'AutomationML/Rack',
      properties: [],
      ioRanges: [],
      children: [{
        id: 'module-1',
        name: 'DI_1',
        path: 'PLC_1.Rack_0.DI_1',
        kind: 'module',
        typeIdentifier: 'AutomationML/Module',
        properties: [{ name: 'Slot', value: '1' }],
        ioRanges: [{
          ioType: 'Input',
          startAddress: 460,
          lengthBits: 56,
          endAddress: 466,
          addressRange: 'I460.0 to I466.7',
        }],
        children: [],
      }],
    }],
  }],
}

const render = async (element: React.ReactNode) => {
  const host = document.createElement('div')
  document.body.appendChild(host)
  const root = createRoot(host)
  await act(async () => root.render(element))
  return { host, root }
}

afterEach(() => {
  document.body.innerHTML = ''
})

describe('HardwareConfigurationView', () => {
  it('shows project devices and child modules in separate panes', async () => {
    const { host } = await render(
      <HardwareConfigurationView
        view={view}
        selectedNodeId="plc-1"
        inspectedNodeId="plc-1"
        onSelectNode={() => undefined}
        onInspectNode={() => undefined}
        onReload={() => undefined}
        reloadBusy={false}
      />,
    )

    expect(host.textContent).toContain('Hardware configuration')
    expect(host.textContent).toContain('PLC_1')
    expect(host.textContent).toContain('Rack_0')
    expect(host.textContent).toContain('Child objects')
  })

  it('folds the left hierarchy, hides leaf nodes there, and inspects right-side children without navigating', async () => {
    const selected: string[] = []
    const inspected: string[] = []
    const { host, root } = await render(
      <HardwareConfigurationView
        view={view}
        selectedNodeId="rack-1"
        inspectedNodeId="rack-1"
        onSelectNode={node => selected.push(node.id)}
        onInspectNode={node => inspected.push(node.id)}
        onReload={() => undefined}
        reloadBusy={false}
      />,
    )

    expect(host.querySelector('[aria-label="Select PLC_1.Rack_0"]')).toBeNull()
    expect(host.querySelector('[aria-expanded="false"]')).not.toBeNull()

    const inspectButton = host.querySelector<HTMLButtonElement>('[aria-label="Inspect PLC_1.Rack_0.DI_1"]')
    expect(inspectButton).not.toBeNull()
    await act(async () => inspectButton?.click())

    expect(inspected).toEqual(['module-1'])
    expect(selected).toEqual([])
    expect(host.textContent).toContain('under PLC_1.Rack_0')

    await act(async () => root.render(
      <HardwareConfigurationView
        view={view}
        selectedNodeId="rack-1"
        inspectedNodeId="module-1"
        onSelectNode={node => selected.push(node.id)}
        onInspectNode={node => inspected.push(node.id)}
      />,
    ))

    expect(host.textContent).toContain('I460.0 to I466.7')
    expect(host.textContent).toContain('DI_1_Tag')
  })

  it('offers a TIA generation action when the saved configuration is missing', async () => {
    const onReload = vi.fn()
    const { host } = await render(
      <HardwareConfigurationView
        view={{
          state: 'missing',
          projectAmlPath: null,
          exportedAt: null,
          message: 'No saved project-level hardware configuration is available. Reload it from TIA.',
          devices: [],
          tags: [],
        }}
        selectedNodeId={null}
        inspectedNodeId={null}
        onSelectNode={() => undefined}
        onInspectNode={() => undefined}
        onReload={onReload}
        reloadBusy={false}
      />,
    )

    const button = host.querySelector<HTMLButtonElement>('[aria-label="Generate hardware configuration from TIA"]')
    expect(button).not.toBeNull()
    expect(button?.textContent).toContain('Generate hardware configuration')

    await act(async () => button?.click())
    expect(onReload).toHaveBeenCalledTimes(1)
  })
})
