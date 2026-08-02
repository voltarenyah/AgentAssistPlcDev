// @vitest-environment happy-dom
import React, { act } from 'react'
import { createRoot } from 'react-dom/client'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import * as api from '@/api/client'
import type { ChatTabsState } from './chatTabState'
import ChatWorkspace from './ChatWorkspace'

globalThis.IS_REACT_ACT_ENVIRONMENT = true

const settingsFixture: api.ChatSettings = {
  model: 'deepseek-v4-flash',
  thinkingEnabled: false,
  reasoningEffort: 'high',
  temperature: 1,
  topP: 1,
  contextWindow: 128000,
}

vi.mock('@/api/client', async importOriginal => {
  const actual = await importOriginal<typeof import('@/api/client')>()
  return {
    ...actual,
    getChatSettings: vi.fn(async () => ({ ...settingsFixture })),
    saveChatSettings: vi.fn(async () => {}),
  }
})

const state: ChatTabsState = {
  activeId: 's2',
  mru: ['s2', 's1'],
  tabs: [
    { sessionId: 's1', title: 'One', messages: [] },
    { sessionId: 's2', title: 'Two', messages: [{ role: 'assistant', content: 'ready', toolCallId: null, timestamp: '2026-07-30T00:00:00Z' }] },
  ],
}

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
})

describe('ChatWorkspace', () => {
  it('keeps inactive panes mounted while hiding them', async () => {
    const { host } = render(
      <ChatWorkspace tabs={state} busy={false} onFocus={vi.fn()} onSend={vi.fn()} onStop={vi.fn()} onContinue={vi.fn()} />,
    )

    expect(host.querySelector('[data-session-pane="s1"]')).not.toBeNull()
    expect(host.querySelector('[data-session-pane="s2"]')).not.toBeNull()
    expect(host.querySelector<HTMLElement>('[data-session-pane="s1"]')?.hidden).toBe(true)
    expect(host.querySelector<HTMLElement>('[data-session-pane="s2"]')?.hidden).toBe(false)
  })

  it('sends text from the active session composer', async () => {
    const onSend = vi.fn()
    const { host } = render(
      <ChatWorkspace tabs={state} busy={false} onFocus={vi.fn()} onSend={onSend} onStop={vi.fn()} onContinue={vi.fn()} />,
    )

    const activePane = host.querySelector<HTMLElement>('[data-session-pane="s2"]')
    const input = activePane!.querySelector<HTMLTextAreaElement>('textarea')
    act(() => {
      input!.value = 'resume this'
      input!.dispatchEvent(new Event('input', { bubbles: true }))
    })
    act(() => {
      host.querySelector<HTMLFormElement>('form[data-chat-composer="s2"]')?.dispatchEvent(
        new Event('submit', { bubbles: true, cancelable: true }),
      )
    })

    expect(onSend).toHaveBeenCalledWith('s2', 'resume this')
  })

  it('shows active-session progress while a message is running', async () => {
    const { host } = render(
      <ChatWorkspace tabs={state} busy={true} onFocus={vi.fn()} onSend={vi.fn()} onStop={vi.fn()} onContinue={vi.fn()} />,
    )

    const activePane = host.querySelector<HTMLElement>('[data-session-pane="s2"]')
    expect(activePane?.textContent).toContain('Assistant is working...')
    expect(activePane?.querySelector<HTMLTextAreaElement>('textarea')?.disabled).toBe(true)
  })

  it('replaces the send button with a stop button while busy and stops on click', async () => {
    const onStop = vi.fn()
    const { host } = render(
      <ChatWorkspace tabs={state} busy={true} onFocus={vi.fn()} onSend={vi.fn()} onStop={onStop} onContinue={vi.fn()} />,
    )

    const activePane = host.querySelector<HTMLElement>('[data-session-pane="s2"]')!
    expect(activePane.querySelector('button[aria-label="Send message"]')).toBeNull()
    const stop = activePane.querySelector<HTMLButtonElement>('button[aria-label="Stop generation"]')
    expect(stop).not.toBeNull()

    act(() => {
      stop!.dispatchEvent(new MouseEvent('click', { bubbles: true }))
    })

    expect(onStop).toHaveBeenCalledTimes(1)
  })

  it('keeps the send button while idle', async () => {
    const { host } = render(
      <ChatWorkspace tabs={state} busy={false} onFocus={vi.fn()} onSend={vi.fn()} onStop={vi.fn()} onContinue={vi.fn()} />,
    )

    const activePane = host.querySelector<HTMLElement>('[data-session-pane="s2"]')!
    expect(activePane.querySelector('button[aria-label="Send message"]')).not.toBeNull()
    expect(activePane.querySelector('button[aria-label="Stop generation"]')).toBeNull()
  })

  it('renders streamed progress messages', async () => {
    const streamed: ChatTabsState = {
      activeId: 's1',
      mru: ['s1'],
      tabs: [
        { sessionId: 's1', title: 'One', messages: [{ role: 'tool', content: '-> get_block({})', toolCallId: null, timestamp: '2026-07-30T00:00:00Z' }] },
      ],
    }
    const { host } = render(
      <ChatWorkspace tabs={streamed} busy={false} onFocus={vi.fn()} onSend={vi.fn()} onStop={vi.fn()} onContinue={vi.fn()} />,
    )

    expect(host.textContent).toContain('Progress')
    expect(host.textContent).toContain('get_block')
  })

  it('renders assistant content as markdown', async () => {
    const markdown: ChatTabsState = {
      activeId: 's1',
      mru: ['s1'],
      tabs: [
        { sessionId: 's1', title: 'One', messages: [{ role: 'assistant', content: '**Bold** and `code`\n\n- one\n- two', toolCallId: null, timestamp: '2026-07-30T00:00:00Z' }] },
      ],
    }
    const { host } = render(
      <ChatWorkspace tabs={markdown} busy={false} onFocus={vi.fn()} onSend={vi.fn()} onStop={vi.fn()} onContinue={vi.fn()} />,
    )

    const body = host.querySelector('.markdown-body')
    expect(body).not.toBeNull()
    expect(body?.querySelector('strong')?.textContent).toBe('Bold')
    expect(body?.querySelector('code')?.textContent).toBe('code')
    expect(body?.querySelectorAll('li').length).toBe(2)
  })

  it('keeps user messages as plain pre-wrap text', async () => {
    const plain: ChatTabsState = {
      activeId: 's1',
      mru: ['s1'],
      tabs: [
        { sessionId: 's1', title: 'One', messages: [{ role: 'user', content: '**not bold**', toolCallId: null, timestamp: '2026-07-30T00:00:00Z' }] },
      ],
    }
    const { host } = render(
      <ChatWorkspace tabs={plain} busy={false} onFocus={vi.fn()} onSend={vi.fn()} onStop={vi.fn()} onContinue={vi.fn()} />,
    )

    expect(host.querySelector('.markdown-body')).toBeNull()
    expect(host.querySelector('strong')).toBeNull()
    expect(host.textContent).toContain('**not bold**')
  })

  it('renders tool progress lines as tool-call cards with the tool name as header', async () => {
    const toolCall: ChatTabsState = {
      activeId: 's1',
      mru: ['s1'],
      tabs: [
        {
          sessionId: 's1',
          title: 'One',
          messages: [
            { role: 'tool', content: 'round 1: calling model', toolCallId: null, timestamp: '2026-07-30T00:00:00Z' },
            { role: 'tool', content: '→ engineering.export_blocks({"deviceId":"plc1"})', toolCallId: null, timestamp: '2026-07-30T00:00:00Z' },
            { role: 'tool', content: '  ✗ engineering.export_blocks: EXPORT_FAILED — TIA busy', toolCallId: null, timestamp: '2026-07-30T00:00:00Z' },
          ],
        },
      ],
    }
    const { host } = render(
      <ChatWorkspace tabs={toolCall} busy={false} onFocus={vi.fn()} onSend={vi.fn()} onStop={vi.fn()} onContinue={vi.fn()} />,
    )

    expect(host.textContent).toContain('engineering.export_blocks')
    expect(host.textContent).toContain('{"deviceId":"plc1"}')
    expect(host.textContent).toContain('EXPORT_FAILED — TIA busy')
    expect(host.textContent).not.toContain('→ engineering')
  })

  it('shows model and think controls below the composer', async () => {
    const { host } = render(
      <ChatWorkspace tabs={state} busy={false} onFocus={vi.fn()} onSend={vi.fn()} onStop={vi.fn()} onContinue={vi.fn()} />,
    )
    await act(async () => {})

    const activePane = host.querySelector<HTMLElement>('[data-session-pane="s2"]')
    const row = activePane?.querySelector<HTMLElement>('[data-chat-settings]')
    expect(row).not.toBeNull()
    expect(row?.querySelector<HTMLSelectElement>('select[aria-label="Model"]')?.value).toBe('deepseek-v4-flash')
    expect(row?.querySelector<HTMLButtonElement>('button[aria-label="Toggle think mode"]')?.getAttribute('aria-pressed')).toBe('false')
    expect(row?.querySelector('select[aria-label="Think effort"]')).toBeNull()
    expect(row?.querySelector<HTMLInputElement>('input[aria-label="Temperature"]')?.value).toBe('1')
    expect(row?.querySelector<HTMLInputElement>('input[aria-label="Top P"]')?.value).toBe('1')
  })

  it('reveals think effort and saves when think mode is toggled', async () => {
    const { host } = render(
      <ChatWorkspace tabs={state} busy={false} onFocus={vi.fn()} onSend={vi.fn()} onStop={vi.fn()} onContinue={vi.fn()} />,
    )
    await act(async () => {})

    const activePane = host.querySelector<HTMLElement>('[data-session-pane="s2"]')
    act(() => {
      activePane?.querySelector<HTMLButtonElement>('button[aria-label="Toggle think mode"]')
        ?.dispatchEvent(new MouseEvent('click', { bubbles: true }))
    })
    expect(activePane?.querySelector('select[aria-label="Think effort"]')).not.toBeNull()
    expect(activePane?.querySelector('input[aria-label="Temperature"]')).toBeNull()
    expect(activePane?.querySelector('input[aria-label="Top P"]')).toBeNull()

    await act(async () => { await new Promise(resolve => setTimeout(resolve, 450)) })
    expect(api.saveChatSettings).toHaveBeenCalledWith({ ...settingsFixture, thinkingEnabled: true })
    expect(activePane?.querySelector('[data-chat-settings-state]')?.textContent).toBe('Saved')
  })

  it('saves the selected model variant', async () => {
    const { host } = render(
      <ChatWorkspace tabs={state} busy={false} onFocus={vi.fn()} onSend={vi.fn()} onStop={vi.fn()} onContinue={vi.fn()} />,
    )
    await act(async () => {})

    const activePane = host.querySelector<HTMLElement>('[data-session-pane="s2"]')
    const modelSelect = activePane?.querySelector<HTMLSelectElement>('select[aria-label="Model"]')
    act(() => {
      modelSelect!.value = 'deepseek-v4-pro'
      modelSelect!.dispatchEvent(new Event('change', { bubbles: true }))
    })

    await act(async () => { await new Promise(resolve => setTimeout(resolve, 450)) })
    expect(api.saveChatSettings).toHaveBeenCalledWith({ ...settingsFixture, model: 'deepseek-v4-pro' })
  })

  it('shows the exact context size with cache breakdown under the composer', async () => {
    const withUsage: ChatTabsState = {
      activeId: 's1',
      mru: ['s1'],
      tabs: [
        {
          sessionId: 's1',
          title: 'One',
          messages: [{ role: 'assistant', content: 'ready', toolCallId: null, timestamp: '2026-07-30T00:00:00Z' }],
          usage: { promptTokens: 22678, completionTokens: 269, totalTokens: 22947, promptCacheHitTokens: 20000, promptCacheMissTokens: 2678 },
        },
      ],
    }
    const { host } = render(
      <ChatWorkspace tabs={withUsage} busy={false} onFocus={vi.fn()} onSend={vi.fn()} onStop={vi.fn()} onContinue={vi.fn()} />,
    )
    await act(async () => {})

    const activePane = host.querySelector<HTMLElement>('[data-session-pane="s1"]')
    expect(activePane?.querySelector('[data-chat-context]')?.textContent)
      .toContain('17.7%')
    expect(activePane?.querySelector('[data-chat-context-progress]')?.getAttribute('aria-valuenow'))
      .toBe('17.7')
  })

  it('hides the context indicator before any billed round', async () => {
    const { host } = render(
      <ChatWorkspace tabs={state} busy={false} onFocus={vi.fn()} onSend={vi.fn()} onStop={vi.fn()} onContinue={vi.fn()} />,
    )
    await act(async () => {})

    const activePane = host.querySelector<HTMLElement>('[data-session-pane="s2"]')
    expect(activePane?.querySelector('[data-chat-context]')).toBeNull()
  })

  it('shows MCP tool success and failure totals in the composer', async () => {
    const withTools: ChatTabsState = {
      activeId: 's1',
      mru: ['s1'],
      tabs: [
        {
          sessionId: 's1',
          title: 'One',
          messages: [
            { role: 'tool', content: '→ engineering.get_block({})', toolCallId: null, timestamp: null },
            { role: 'tool', content: '→ engineering.get_schema({})\n  ✗ engineering.get_schema: TOOL_FAILED — unavailable', toolCallId: null, timestamp: null },
          ],
        },
      ],
    }
    const { host } = render(
      <ChatWorkspace tabs={withTools} busy={false} onFocus={vi.fn()} onSend={vi.fn()} onStop={vi.fn()} onContinue={vi.fn()} />,
    )
    await act(async () => {})

    expect(host.querySelector('[data-chat-tool-stats]')?.textContent)
      .toBe('tools: 1 succeeded / 1 failed')
  })

  it('offers continue only after a turn that hit the round cap', async () => {
    const capped: ChatTabsState = {
      activeId: 's1',
      mru: ['s1'],
      tabs: [
        {
          sessionId: 's1',
          title: 'One',
          messages: [{ role: 'assistant', content: 'partial', toolCallId: null, timestamp: '2026-07-30T00:00:00Z' }],
          hitRoundCap: true,
        },
      ],
    }
    const onContinue = vi.fn()
    const { host } = render(
      <ChatWorkspace tabs={capped} busy={false} onFocus={vi.fn()} onSend={vi.fn()} onStop={vi.fn()} onContinue={onContinue} />,
    )

    const button = host.querySelector<HTMLButtonElement>('[data-round-cap="s1"] button')
    expect(button?.textContent).toContain('Continue')
    act(() => {
      button!.dispatchEvent(new MouseEvent('click', { bubbles: true }))
    })
    expect(onContinue).toHaveBeenCalledWith('s1')
  })

  it('hides the continue affordance for turns that finished normally', async () => {
    const { host } = render(
      <ChatWorkspace tabs={state} busy={false} onFocus={vi.fn()} onSend={vi.fn()} onStop={vi.fn()} onContinue={vi.fn()} />,
    )

    expect(host.querySelector('[data-round-cap]')).toBeNull()
  })
})
