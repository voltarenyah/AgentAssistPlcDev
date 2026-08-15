// @vitest-environment happy-dom
import React, { act } from 'react'
import { createRoot } from 'react-dom/client'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import MainStudio from './MainStudio'

globalThis.IS_REACT_ACT_ENVIRONMENT = true

const keyState = vi.hoisted(() => ({ configured: false }))
const balanceRequest = vi.hoisted(() => vi.fn(async () => ({
  isAvailable: true,
  balances: [{ currency: 'USD', totalBalance: '10.42', grantedBalance: '0.00', toppedUpBalance: '10.42' }],
  fetchedAt: '2026-08-02T00:00:00.000Z',
})))

vi.mock('@/api/client', async importOriginal => {
  const actual = await importOriginal<typeof import('@/api/client')>()
  return {
    ...actual,
    listWorkbenches: vi.fn(async () => []),
    getSessions: vi.fn(async () => []),
    getKeyStatus: vi.fn(async () => ({ configured: keyState.configured })),
    getDeepSeekBalance: balanceRequest,
  }
})

const render = (element: React.ReactNode) => {
  const host = document.createElement('div')
  document.body.appendChild(host)
  const root = createRoot(host)
  act(() => root.render(element))
  return { host, root }
}

afterEach(() => {
  document.body.innerHTML = ''
})

beforeEach(() => {
  vi.clearAllMocks()
  keyState.configured = false
  balanceRequest.mockClear()
  window.localStorage.clear()
})

describe('MainStudio API key entrance', () => {
  it('warns in the title bar when no API key is configured', async () => {
    const { host } = render(<MainStudio />)
    await act(async () => {})

    const indicator = host.querySelector<HTMLElement>('[data-api-status]')
    expect(indicator?.textContent).toContain('No valid API key')
    expect(host.querySelector('header')?.textContent).not.toContain('No valid API key')
    expect(host.querySelector('footer')?.textContent).toContain('0 TIA sessions')
  })

  it('shows API online when a key is configured', async () => {
    keyState.configured = true
    const { host } = render(<MainStudio />)
    await act(async () => {})

    const indicator = host.querySelector<HTMLElement>('[data-api-status]')
    expect(indicator?.textContent).toContain('API online')
  })

  it('loads the DeepSeek balance during startup and shows it in the status bar', async () => {
    keyState.configured = true
    const { host } = render(<MainStudio />)
    await act(async () => {})

    expect(balanceRequest).toHaveBeenCalledTimes(1)
    expect(host.querySelector('[data-api-balance]')?.textContent).toContain('$10.42')
  })

  it('shows refresh progress and completion feedback in the status bar', async () => {
    keyState.configured = true
    const { host } = render(<MainStudio />)
    await act(async () => {})

    const nextBalance = {
      isAvailable: true,
      balances: [{ currency: 'USD', totalBalance: '9.87', grantedBalance: '0.00', toppedUpBalance: '9.87' }],
      fetchedAt: '2026-08-15T08:31:00.000Z',
    }
    let resolvePending: ((value: typeof nextBalance) => void) | undefined
    const pending = new Promise<typeof nextBalance>(resolve => { resolvePending = resolve })
    balanceRequest.mockImplementationOnce(() => pending)

    act(() => host.querySelector<HTMLButtonElement>('[aria-label="Refresh DeepSeek balance"]')?.click())
    expect(host.querySelector('[data-balance-refresh-status]')?.textContent).toContain('Refreshing')
    expect(host.querySelector<HTMLButtonElement>('[aria-label="Refresh DeepSeek balance"]')?.disabled).toBe(true)

    await act(async () => {
      resolvePending?.(nextBalance)
      await pending
    })
    expect(host.querySelector('[data-balance-refresh-status]')?.textContent).not.toContain('Updated')
    expect(host.querySelector('[data-api-balance]')?.getAttribute('title')).toContain('Fetched')
    expect(host.querySelector('[data-api-balance]')?.textContent).toContain('$9.87')
  })

  it('keeps the status bar and settings entry point available with both docks collapsed', async () => {
    const { host } = render(<MainStudio />)
    await act(async () => {})

    act(() => host.querySelector<HTMLButtonElement>('[data-dock-toggle="left"]')?.click())
    act(() => host.querySelector<HTMLButtonElement>('[data-dock-toggle="right"]')?.click())

    expect(host.querySelector('[data-dock="left"]')?.getAttribute('data-dock-state')).toBe('closed')
    expect(host.querySelector('[data-dock="right"]')).toBeNull()
    expect(host.querySelector('[data-status-bar]')).not.toBeNull()

    act(() => host.querySelector<HTMLButtonElement>('[aria-label="Settings"]')?.click())
    await act(async () => {})
    expect(host.querySelector('[data-settings-page]')).not.toBeNull()
  })
})
