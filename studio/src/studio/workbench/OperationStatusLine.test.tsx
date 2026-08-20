// @vitest-environment happy-dom
import React, { act } from 'react'
import { createRoot } from 'react-dom/client'
import { afterEach, describe, expect, it } from 'vitest'
import OperationStatusLine from './OperationStatusLine'
import type { OperationStatus } from '@/api/client'

globalThis.IS_REACT_ACT_ENVIRONMENT = true

const status = (message: string, state: OperationStatus['state'] = 'running'): OperationStatus => ({
  operationId: 'op-1',
  operationType: 'compare-tia',
  state,
  message,
  updatedAt: new Date().toISOString(),
  errorMessage: null,
})

const render = async (value: OperationStatus | null) => {
  const host = document.createElement('div')
  document.body.appendChild(host)
  const root = createRoot(host)
  await act(async () => root.render(<OperationStatusLine status={value} />))
  return { host, root }
}

afterEach(() => {
  document.body.innerHTML = ''
})

describe('OperationStatusLine', () => {
  it('shows a progress ring with the exported percentage when the message carries totals', async () => {
    const { host } = await render(status('Exported PLC source files: 120 of 340'))

    const ring = host.querySelector('[role="progressbar"]')
    expect(ring).not.toBeNull()
    expect(ring!.getAttribute('aria-valuenow')).toBe('35')
    expect(host.textContent).toContain('35%')
    expect(host.textContent).toContain('Exported PLC source files: 120 of 340')
  })

  it('keeps the spinner for running messages without a known total', async () => {
    const { host } = await render(status('Exporting block Main_OB1...'))

    expect(host.querySelector('[role="progressbar"]')).toBeNull()
    expect(host.querySelector('.animate-spin')).not.toBeNull()
  })

  it('shows the success state without a ring once the operation completes', async () => {
    const { host } = await render(status('TIA comparison completed.', 'succeeded'))

    expect(host.querySelector('[role="progressbar"]')).toBeNull()
    expect(host.textContent).toContain('TIA comparison completed.')
  })
})
