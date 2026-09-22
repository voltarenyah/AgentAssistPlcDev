// @vitest-environment happy-dom
import React, { act } from 'react'
import { createRoot } from 'react-dom/client'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import * as api from '@/api/client'
import SettingsPage from './SettingsPage'
import { THEME_STORAGE_KEY, resetThemeCacheForTests } from '@/studio/theme'

globalThis.IS_REACT_ACT_ENVIRONMENT = true

const settingsFixture: api.ChatSettings = {
  model: 'deepseek-v4-flash',
  thinkingEnabled: false,
  reasoningEffort: 'high',
  temperature: 1,
  topP: 1,
  contextWindow: 128000,
  roundLimit: 24,
}

const statusFixture: api.ServerStatus = {
  storage: 'workbench',
  legacyProjects: false,
  version: '1.0.0+test',
}

vi.mock('@/api/client', async importOriginal => {
  const actual = await importOriginal<typeof import('@/api/client')>()
  return {
    ...actual,
    getChatSettings: vi.fn(async () => ({ ...settingsFixture })),
    saveChatSettings: vi.fn(async () => {}),
    getKeyStatus: vi.fn(async () => ({ configured: true })),
    saveApiKey: vi.fn(async () => {}),
    getDeepSeekBalance: vi.fn(async () => ({
      isAvailable: true,
      balances: [{ currency: 'USD', totalBalance: '9.99', grantedBalance: '0', toppedUpBalance: '9.99' }],
      fetchedAt: '2026-08-14T00:00:00Z',
    })),
    getStatus: vi.fn(async () => ({ ...statusFixture })),
    getAppAssistantHealth: vi.fn(async () => ({ status: 'ok', model: 'deepseek-v4-flash', modelMode: 'llm' })),
    getSessions: vi.fn(async () => [{ id: 1, mode: 'attached', projectPath: 'C:\\Demo\\demo.ap17' }]),
    getTools: vi.fn(async () => [
      { name: 'demo_tool', description: null, serverName: 'demo', schema: {}, tier: 'read' },
    ]),
  }
})

const render = async (element: React.ReactNode) => {
  const host = document.createElement('div')
  document.body.appendChild(host)
  const root = createRoot(host)
  await act(async () => { root.render(element) })
  return { host, root }
}

const typeText = async (input: HTMLInputElement, value: string) => {
  const setValue = Object.getOwnPropertyDescriptor(window.HTMLInputElement.prototype, 'value')!.set!
  await act(async () => {
    setValue.call(input, value)
    input.dispatchEvent(new Event('input', { bubbles: true }))
  })
}

const clickCategory = async (host: HTMLElement, id: string) => {
  const button = host.querySelector<HTMLElement>(`[data-settings-category="${id}"]`)
  expect(button, `category ${id}`).not.toBeNull()
  await act(async () => { button!.dispatchEvent(new MouseEvent('click', { bubbles: true })) })
}

beforeEach(() => {
  vi.clearAllMocks()
  resetThemeCacheForTests()
  window.localStorage.clear()
})

afterEach(() => {
  document.body.innerHTML = ''
  document.documentElement.classList.remove('dark')
})

describe('SettingsPage', () => {
  it('renders the sidebar categories and the General page by default', async () => {
    const { host, root } = await render(<SettingsPage onClose={vi.fn()} />)

    expect(host.querySelector('[data-settings-page]')).not.toBeNull()
    for (const id of ['general', 'assistant', 'agent-loop', 'appearance', 'about']) {
      expect(host.querySelector(`[data-settings-category="${id}"]`), id).not.toBeNull()
    }
    expect(host.textContent).toContain('Application status')
    expect(host.textContent).toContain('1 MCP tools')

    await act(async () => root.unmount())
  })

  it('filters categories through the search box', async () => {
    const { host, root } = await render(<SettingsPage onClose={vi.fn()} />)
    const search = host.querySelector<HTMLInputElement>('input[aria-label="Search settings"]')!

    await typeText(search, 'theme')

    expect(host.querySelector('[data-settings-category="appearance"]')).not.toBeNull()
    expect(host.querySelector('[data-settings-category="general"]')).toBeNull()

    await act(async () => root.unmount())
  })

  it('renders the current model, leaving the change-and-save interaction to the browser', async () => {
    const { host, root } = await render(<SettingsPage onClose={vi.fn()} />)
    await clickCategory(host, 'assistant')

    // notion-kit's Select cannot be driven in happy-dom - pointer, click, keyboard and setting
    // a hidden native select's value were all tried - and Base UI renders both the label and
    // the role client-side, so this environment can only assert that the control is present.
    // That makes the browser check the load-bearing one for this control, not a supplement:
    // it proves the trigger's role, the label SelectValue renders from the items collection,
    // and that choosing another model saves through the API. What the unit test no longer
    // covers is any part of the model selection. The save path itself stays covered, because
    // the thinking-mode switch and the numeric field tests below still drive changeSettings
    // through to the API.
    expect(host.querySelector('[aria-label="Model"]')).not.toBeNull()

    await act(async () => root.unmount())
  })

  it('toggles thinking mode through the switch', async () => {
    const { host, root } = await render(<SettingsPage onClose={vi.fn()} />)
    await clickCategory(host, 'assistant')

    const thinking = host.querySelector<HTMLElement>('[role="switch"][aria-label="Thinking mode"]')!
    await act(async () => { thinking.dispatchEvent(new MouseEvent('click', { bubbles: true })) })

    expect(api.saveChatSettings).toHaveBeenCalledWith({ ...settingsFixture, thinkingEnabled: true })

    await act(async () => root.unmount())
  })

  it('edits an agent-loop numeric field', async () => {
    const { host, root } = await render(<SettingsPage onClose={vi.fn()} />)
    await clickCategory(host, 'agent-loop')

    const input = host.querySelector<HTMLInputElement>('input[aria-label="Round limit"]')!
    expect(input.value).toBe('24')

    await typeText(input, '48')

    expect(api.saveChatSettings).toHaveBeenCalledWith({ ...settingsFixture, roundLimit: 48 })

    await act(async () => root.unmount())
  })

  it('persists the theme from the Appearance page', async () => {
    const { host, root } = await render(<SettingsPage onClose={vi.fn()} />)
    await clickCategory(host, 'appearance')

    const darkMode = host.querySelector<HTMLElement>('[role="switch"][aria-label="Dark mode"]')!
    // Base UI (notion-kit) exposes the ARIA state rather than Radix's data-state,
    // so assert the accessible contract instead of either library's data attribute.
    expect(darkMode.getAttribute('aria-checked')).toBe('true')
    await act(async () => { darkMode.dispatchEvent(new MouseEvent('click', { bubbles: true })) })

    expect(window.localStorage.getItem(THEME_STORAGE_KEY)).toBe('light')
    expect(document.documentElement.classList.contains('dark')).toBe(false)

    await act(async () => root.unmount())
  })

  it('opens the UI component catalog from Appearance', async () => {
    const onOpenComponentCatalog = vi.fn()
    const { host, root } = await render(<SettingsPage onClose={vi.fn()} onOpenComponentCatalog={onOpenComponentCatalog} />)
    await clickCategory(host, 'appearance')

    const openCatalog = host.querySelector<HTMLButtonElement>('[data-open-component-catalog]')!
    await act(async () => { openCatalog.click() })

    expect(onOpenComponentCatalog).toHaveBeenCalledTimes(1)
    await act(async () => root.unmount())
  })

  it('saves a new API key and refreshes status', async () => {
    const { host, root } = await render(<SettingsPage onClose={vi.fn()} />)
    await clickCategory(host, 'assistant')

    const keyInput = host.querySelector<HTMLInputElement>('input[aria-label="DeepSeek API key"]')!
    await typeText(keyInput, 'sk-test-key')
    const saveButton = Array.from(host.querySelectorAll('button')).find(button => button.textContent === 'Save key')!
    await act(async () => { saveButton.dispatchEvent(new MouseEvent('click', { bubbles: true })) })

    expect(api.saveApiKey).toHaveBeenCalledWith('sk-test-key')

    await act(async () => root.unmount())
  })

  it('invokes onClose from Back to app and onResetLayout from the reset button', async () => {
    const onClose = vi.fn()
    const onResetLayout = vi.fn()
    const { host, root } = await render(<SettingsPage onClose={onClose} onResetLayout={onResetLayout} />)

    const back = Array.from(host.querySelectorAll('button')).find(button => button.textContent?.includes('Back to app'))!
    await act(async () => { back.dispatchEvent(new MouseEvent('click', { bubbles: true })) })
    expect(onClose).toHaveBeenCalledTimes(1)

    const reset = host.querySelector<HTMLElement>('[data-reset-layout]')!
    await act(async () => { reset.dispatchEvent(new MouseEvent('click', { bubbles: true })) })
    expect(onResetLayout).toHaveBeenCalledTimes(1)

    await act(async () => root.unmount())
  })
})
