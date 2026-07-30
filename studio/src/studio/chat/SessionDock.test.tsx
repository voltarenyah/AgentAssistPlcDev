// @vitest-environment happy-dom
import React, { act } from 'react'
import { createRoot } from 'react-dom/client'
import { afterEach, describe, expect, it, vi } from 'vitest'
import type { ChatSessionInfo } from '@/api/client'
import SessionDock from './SessionDock'

globalThis.IS_REACT_ACT_ENVIRONMENT = true

const sessions: ChatSessionInfo[] = [
  {
    sessionId: 's1',
    title: 'Startup checks',
    projectName: 'PLC_1',
    createdAt: '2026-07-30T00:00:00Z',
    updatedAt: '2026-07-30T01:00:00Z',
    messageCount: 2,
    turnCount: 1,
    firstUserMessage: 'Check valves',
  },
]

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

describe('SessionDock', () => {
  it('activates a saved session from the dock list', async () => {
    const onActivate = vi.fn()
    const { host } = render(
      <SessionDock
        sessions={sessions}
        activeSessionId={null}
        busy={false}
        hidden={false}
        onCreate={vi.fn()}
        onActivate={onActivate}
        onRename={vi.fn()}
        onRemove={vi.fn()}
        onExport={vi.fn()}
      />,
    )

    act(() => host.querySelector<HTMLButtonElement>('[data-session-id="s1"]')?.click())

    expect(onActivate).toHaveBeenCalledWith('s1')
  })

  it('exports a saved session as markdown', async () => {
    const onExport = vi.fn()
    const { host } = render(
      <SessionDock
        sessions={sessions}
        activeSessionId={null}
        busy={false}
        hidden={false}
        onCreate={vi.fn()}
        onActivate={vi.fn()}
        onRename={vi.fn()}
        onRemove={vi.fn()}
        onExport={onExport}
      />,
    )

    act(() => host.querySelector<HTMLButtonElement>('[aria-label="Export Startup checks"]')?.click())

    expect(onExport).toHaveBeenCalledWith('s1')
  })

  it('submits a trimmed inline rename', async () => {
    const onRename = vi.fn()
    const { host } = render(
      <SessionDock
        sessions={sessions}
        activeSessionId={null}
        busy={false}
        hidden={false}
        onCreate={vi.fn()}
        onActivate={vi.fn()}
        onRename={onRename}
        onRemove={vi.fn()}
        onExport={vi.fn()}
      />,
    )

    act(() => host.querySelector<HTMLButtonElement>('[aria-label="Rename Startup checks"]')?.click())
    const input = host.querySelector<HTMLInputElement>('input[name="session-title"]')
    act(() => {
      input!.value = '  Valve diagnosis  '
      input!.dispatchEvent(new Event('input', { bubbles: true }))
    })
    act(() => {
      host.querySelector<HTMLFormElement>('form[data-session-rename="s1"]')?.dispatchEvent(
        new Event('submit', { bubbles: true, cancelable: true }),
      )
    })

    expect(onRename).toHaveBeenCalledWith('s1', 'Valve diagnosis')
  })
})
