// @vitest-environment happy-dom
import React, { act } from 'react'
import { createRoot } from 'react-dom/client'
import { afterEach, describe, expect, it } from 'vitest'
import * as api from '@/api/client'
import HardwareNetworkView from './HardwareNetworkView'

globalThis.IS_REACT_ACT_ENVIRONMENT = true

const node = (overrides: Partial<api.HardwareNetworkNode>): api.HardwareNetworkNode => ({
  id: 'node-1',
  address: '192.168.1.11',
  subnetMask: '255.255.255.0',
  profinetDeviceName: 'cof-lid-a',
  deviceName: 'COF-LID-A',
  devicePath: 'Project.COF-BASE-A_4.COF-LID-A',
  interfaceLabel: 'X1',
  subnetName: 'PN/IE_1',
  ...overrides,
})

const view: api.HardwareNetworkView = {
  state: 'available',
  exportedAt: '2026-08-04T00:00:00Z',
  message: null,
  nodes: [
    node({ id: 'node-high', address: '192.168.1.101', deviceName: 'HMI_1' }),
    node({}),
    node({ id: 'node-low', address: '192.168.1.2', deviceName: 'PLC_1', profinetDeviceName: 'plc-1' }),
    node({ id: 'node-l2', address: '10.10.0.5', deviceName: 'Drive_1', subnetName: 'PN/IE_Level2' }),
    node({ id: 'node-free', address: '172.16.0.9', deviceName: 'Panel_1', subnetName: null, subnetMask: null, profinetDeviceName: null, interfaceLabel: null }),
  ],
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

describe('HardwareNetworkView', () => {
  it('groups nodes under their subnet and keeps unlinked nodes last', async () => {
    const { host, root } = await render(<HardwareNetworkView view={view} />)

    const headers = [...host.querySelectorAll('section > div span')].map(span => span.textContent)
    const subnetOrder = headers.filter(text => text === 'PN/IE_1' || text === 'PN/IE_Level2' || text === '— Unlinked')
    expect(subnetOrder).toEqual(['PN/IE_1', 'PN/IE_Level2', '— Unlinked'])
    expect(host.textContent).toContain('5 nodes')
    expect(host.textContent).toContain('2 subnets')

    await act(async () => root.unmount())
  })

  it('sorts addresses numerically within a subnet and shows node details', async () => {
    const { host, root } = await render(<HardwareNetworkView view={view} />)

    const addresses = [...host.querySelectorAll('section:first-of-type tbody td:first-child')]
      .map(cell => cell.textContent)
    expect(addresses).toEqual(['192.168.1.2', '192.168.1.11', '192.168.1.101'])
    expect(host.textContent).toContain('cof-lid-a')
    expect(host.textContent).toContain('255.255.255.0')
    expect(host.textContent).toContain('Project.COF-BASE-A_4.COF-LID-A')

    await act(async () => root.unmount())
  })

  it('shows the empty state message when the view is unavailable', async () => {
    const { host, root } = await render(
      <HardwareNetworkView view={{ state: 'missing', exportedAt: null, nodes: [], message: 'No saved hardware export.' }} />,
    )

    expect(host.textContent).toContain('No saved hardware export.')

    await act(async () => root.unmount())
  })
})
