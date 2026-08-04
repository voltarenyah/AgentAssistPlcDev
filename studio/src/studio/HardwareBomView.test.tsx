// @vitest-environment happy-dom
import React, { act } from 'react'
import { createRoot } from 'react-dom/client'
import { afterEach, describe, expect, it } from 'vitest'
import * as api from '@/api/client'
import HardwareBomView from './HardwareBomView'

globalThis.IS_REACT_ACT_ENVIRONMENT = true

const item = (overrides: Partial<api.HardwareBomItem>): api.HardwareBomItem => ({
  id: 'item-1',
  name: 'COF-LID-A',
  path: 'Project.COF-BASE-A_4.Châssis_0.COF-LID-A',
  position: 'Project / COF-BASE-A_4 / Châssis_0',
  positionNumber: 0,
  typeName: 'IM 155-6 PN ST',
  typeIdentifier: 'OrderNumber:6ES7 155-6AU01-0BN0',
  orderNumber: '6ES7 155-6AU01-0BN0',
  firmwareVersion: 'V4.2',
  ...overrides,
})

const view: api.HardwareBomView = {
  state: 'available',
  exportedAt: '2026-08-04T00:00:00Z',
  message: null,
  items: [
    item({}),
    item({
      id: 'item-2',
      name: '1200MOD2',
      path: 'Project.COF-BASE-A_4.Châssis_0.COF-LID-A.1200MOD2',
      position: 'Project / COF-BASE-A_4 / Châssis_0 / COF-LID-A',
      positionNumber: 1,
      typeName: 'AI 4xRTD/TC 2-,3-,4-wire HF',
      typeIdentifier: 'OrderNumber:6ES7 134-6JD00-0CA1',
      orderNumber: '6ES7 134-6JD00-0CA1',
      firmwareVersion: 'V2.0',
    }),
    item({
      id: 'item-3',
      name: 'COF-LID-B',
      path: 'Project.COF-BASE-B_1.Châssis_0.COF-LID-B',
      position: 'Project / COF-BASE-B_1 / Châssis_0',
      firmwareVersion: 'V4.2',
    }),
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

describe('HardwareBomView', () => {
  it('lists every component with position, order number and firmware', async () => {
    const { host, root } = await render(<HardwareBomView view={view} />)

    expect(host.textContent).toContain('3 components')
    expect(host.textContent).toContain('2 types')
    expect(host.textContent).toContain('COF-LID-A')
    expect(host.textContent).toContain('Project / COF-BASE-A_4 / Châssis_0')
    expect(host.textContent).toContain('6ES7 155-6AU01-0BN0')
    expect(host.textContent).toContain('6ES7 134-6JD00-0CA1')
    expect(host.textContent).toContain('IM 155-6 PN ST')
    expect(host.textContent).toContain('V2.0')

    await act(async () => root.unmount())
  })

  it('narrows the rows when a filter is entered', async () => {
    const { host, root } = await render(<HardwareBomView view={view} />)
    const input = host.querySelector('input[aria-label="Filter components"]') as HTMLInputElement
    const setValue = Object.getOwnPropertyDescriptor(window.HTMLInputElement.prototype, 'value')!.set!

    await act(async () => {
      setValue.call(input, '134-6JD00')
      input.dispatchEvent(new Event('input', { bubbles: true }))
    })

    expect(host.textContent).toContain('1200MOD2')
    expect(host.textContent).not.toContain('COF-LID-B')

    await act(async () => root.unmount())
  })

  it('aggregates quantities per type when grouping is enabled', async () => {
    const { host, root } = await render(<HardwareBomView view={view} />)
    const toggle = host.querySelector('button[aria-label="Toggle group by type"]') as HTMLButtonElement

    await act(async () => toggle.dispatchEvent(new MouseEvent('click', { bubbles: true })))

    // The duplicated head-module type collapses into one grouped row with qty 2.
    const rows = [...host.querySelectorAll('tbody tr')]
    expect(rows).toHaveLength(2)
    const groupRow = rows.find(row => row.textContent?.includes('OrderNumber:6ES7 155-6AU01-0BN0'))
    expect(groupRow?.textContent).toContain('2')
    expect(host.textContent).not.toContain('Project / COF-BASE-A_4 / Châssis_0')

    // Expanding the group reveals the individual placements.
    await act(async () => groupRow!.dispatchEvent(new MouseEvent('click', { bubbles: true })))
    expect(host.textContent).toContain('COF-LID-B')
    expect(host.textContent).toContain('Project / COF-BASE-B_1 / Châssis_0')

    await act(async () => root.unmount())
  })

  it('shows the empty state message when the view is unavailable', async () => {
    const { host, root } = await render(
      <HardwareBomView view={{ state: 'missing', exportedAt: null, items: [], message: 'No saved hardware export.' }} />,
    )

    expect(host.textContent).toContain('No saved hardware export.')

    await act(async () => root.unmount())
  })
})
