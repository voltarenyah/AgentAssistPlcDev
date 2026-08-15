// @vitest-environment happy-dom
import React from 'react'
import { act } from 'react'
import { createRoot } from 'react-dom/client'
import { afterEach, describe, expect, it, vi } from 'vitest'
import DeepSeekBalanceStatus from './DeepSeekBalanceStatus'

globalThis.IS_REACT_ACT_ENVIRONMENT = true

const balance = {
  isAvailable: true,
  balances: [{ currency: 'USD', totalBalance: '10.42', grantedBalance: '0.00', toppedUpBalance: '10.42' }],
  fetchedAt: '2026-08-15T08:30:00.000Z',
}

describe('DeepSeekBalanceStatus', () => {
  afterEach(() => {
    document.body.innerHTML = ''
  })

  it('shows an explicit in-progress state and disables duplicate refreshes', () => {
    const host = document.createElement('div')
    document.body.appendChild(host)
    const onRefresh = vi.fn()

    act(() => createRoot(host).render(<DeepSeekBalanceStatus balance={balance} state="refreshing" onRefresh={onRefresh} />))

    const button = host.querySelector<HTMLButtonElement>('[aria-label="Refresh DeepSeek balance"]')
    expect(button?.disabled).toBe(true)
    expect(button?.getAttribute('aria-busy')).toBe('true')
    expect(host.querySelector('[data-balance-refresh-status]')?.textContent).toContain('Refreshing')
  })

  it('keeps the completed timestamp in the hover tooltip instead of the status bar text', () => {
    const host = document.createElement('div')
    document.body.appendChild(host)
    act(() => createRoot(host).render(<DeepSeekBalanceStatus balance={balance} state="success" onRefresh={() => {}} />))

    expect(host.querySelector('[data-balance-refresh-status]')?.textContent).not.toContain('Updated')
    expect(host.querySelector('[data-api-balance]')?.getAttribute('title')).toContain('Fetched')
  })

  it('shows a retryable failure state when the refresh fails', () => {
    const host = document.createElement('div')
    document.body.appendChild(host)
    act(() => createRoot(host).render(<DeepSeekBalanceStatus balance={balance} state="error" onRefresh={() => {}} />))

    expect(host.querySelector('[data-balance-refresh-status]')?.textContent).toContain('Refresh failed')
  })
})
