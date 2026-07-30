// @vitest-environment happy-dom
import React, { act } from 'react'
import { createRoot } from 'react-dom/client'
import { afterEach, describe, expect, it, vi } from 'vitest'
import type { ChatTabsState } from './chatTabState'
import ChatWorkspace from './ChatWorkspace'

globalThis.IS_REACT_ACT_ENVIRONMENT = true

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

describe('ChatWorkspace', () => {
  it('keeps inactive panes mounted while hiding them', async () => {
    const { host } = render(
      <ChatWorkspace tabs={state} busy={false} onFocus={vi.fn()} onSend={vi.fn()} />,
    )

    expect(host.querySelector('[data-session-pane="s1"]')).not.toBeNull()
    expect(host.querySelector('[data-session-pane="s2"]')).not.toBeNull()
    expect(host.querySelector<HTMLElement>('[data-session-pane="s1"]')?.hidden).toBe(true)
    expect(host.querySelector<HTMLElement>('[data-session-pane="s2"]')?.hidden).toBe(false)
  })

  it('sends text from the active session composer', async () => {
    const onSend = vi.fn()
    const { host } = render(
      <ChatWorkspace tabs={state} busy={false} onFocus={vi.fn()} onSend={onSend} />,
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
      <ChatWorkspace tabs={state} busy={true} onFocus={vi.fn()} onSend={vi.fn()} />,
    )

    const activePane = host.querySelector<HTMLElement>('[data-session-pane="s2"]')
    expect(activePane?.textContent).toContain('Assistant is working...')
    expect(activePane?.querySelector<HTMLTextAreaElement>('textarea')?.disabled).toBe(true)
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
      <ChatWorkspace tabs={streamed} busy={false} onFocus={vi.fn()} onSend={vi.fn()} />,
    )

    expect(host.textContent).toContain('Progress')
    expect(host.textContent).toContain('get_block')
  })
})
