// @vitest-environment happy-dom
import React, { act } from 'react'
import { createRoot } from 'react-dom/client'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import * as api from '@/api/client'
import MainStudio from './MainStudio'

globalThis.IS_REACT_ACT_ENVIRONMENT = true

const keyState = vi.hoisted(() => ({ configured: false }))

vi.mock('@/api/client', async importOriginal => {
  const actual = await importOriginal<typeof import('@/api/client')>()
  return {
    ...actual,
    listWorkbenches: vi.fn(async () => []),
    getSessions: vi.fn(async () => []),
    getKeyStatus: vi.fn(async () => ({ configured: keyState.configured })),
    saveApiKey: vi.fn(async () => { keyState.configured = true }),
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
  window.localStorage.clear()
})

describe('MainStudio API key entrance', () => {
  it('warns in the title bar when no API key is configured', async () => {
    const { host } = render(<MainStudio />)
    await act(async () => {})

    const indicator = host.querySelector<HTMLButtonElement>('[data-api-status]')
    expect(indicator?.textContent).toContain('No valid API key')
  })

  it('shows API online when a key is configured', async () => {
    keyState.configured = true
    const { host } = render(<MainStudio />)
    await act(async () => {})

    const indicator = host.querySelector<HTMLButtonElement>('[data-api-status]')
    expect(indicator?.textContent).toContain('API online')
  })

  it('saves a key from the dialog and refreshes the indicator', async () => {
    const { host } = render(<MainStudio />)
    await act(async () => {})

    act(() => {
      host.querySelector<HTMLButtonElement>('[data-api-status]')
        ?.dispatchEvent(new MouseEvent('click', { bubbles: true }))
    })
    const keyInput = host.querySelector<HTMLInputElement>('input[type="password"]')
    expect(keyInput).not.toBeNull()

    act(() => {
      const setValue = Object.getOwnPropertyDescriptor(window.HTMLInputElement.prototype, 'value')!.set!
      setValue.call(keyInput, 'sk-test-key')
      keyInput!.dispatchEvent(new Event('input', { bubbles: true }))
    })
    const saveButton = Array.from(host.querySelectorAll<HTMLButtonElement>('button'))
      .find(button => button.textContent?.includes('Save key'))
    act(() => {
      saveButton?.dispatchEvent(new MouseEvent('click', { bubbles: true }))
    })
    await act(async () => {})

    expect(api.saveApiKey).toHaveBeenCalledWith('sk-test-key')
    expect(host.querySelector('input[type="password"]')).toBeNull()
    expect(host.querySelector<HTMLButtonElement>('[data-api-status]')?.textContent).toContain('API online')
  })

  it('keeps the status bar and settings entry point available with both docks collapsed', async () => {
    const { host } = render(<MainStudio />)
    await act(async () => {})

    act(() => host.querySelector<HTMLButtonElement>('[data-dock-toggle="left"]')?.click())
    act(() => host.querySelector<HTMLButtonElement>('[data-dock-toggle="right"]')?.click())

    expect(host.querySelector('[data-dock="left"]')).toBeNull()
    expect(host.querySelector('[data-dock="right"]')).toBeNull()
    expect(host.querySelector('[data-status-bar]')).not.toBeNull()

    act(() => host.querySelector<HTMLButtonElement>('[aria-label="Settings"]')?.click())
    expect(host.querySelector('input[type="password"]')).not.toBeNull()
  })
})
