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
    taskId: null,
  },
]
const assignedSession = { ...sessions[0], sessionId: 's2', title: 'Assigned checks', taskId: 'task-1' }
const manualSession = { ...sessions[0], sessionId: 's3', title: 'Manual checks', taskId: 'task-2', taskProvenance: 'manual' as const }

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
        onSetTask={vi.fn()}
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
        onSetTask={vi.fn()}
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
        onSetTask={vi.fn()}
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

  it('exposes accessible attach and remove task controls', async () => {
    const onSetTask = vi.fn()
    const originalPrompt = window.prompt
    window.prompt = () => 'task-42'
    const { host } = render(
      <SessionDock sessions={sessions} activeSessionId={null} busy={false} hidden={false}
        onCreate={vi.fn()} onActivate={vi.fn()} onRename={vi.fn()} onRemove={vi.fn()}
        onExport={vi.fn()} onSetTask={onSetTask} />,
    )
    act(() => host.querySelector<HTMLButtonElement>('[aria-label="Attach task for Startup checks"]')?.click())
    expect(onSetTask).toHaveBeenCalledWith('s1', 'task-42')
    window.prompt = originalPrompt
  })

  it('labels legacy sessions and invokes reassignment and removal controls', () => {
    const onSetTask = vi.fn()
    const originalPrompt = window.prompt
    window.prompt = () => 'replacement-task'
    const { host } = render(<SessionDock sessions={[sessions[0], assignedSession, manualSession]} activeSessionId={null} busy={false} hidden={false}
      onCreate={vi.fn()} onActivate={vi.fn()} onRename={vi.fn()} onRemove={vi.fn()} onExport={vi.fn()} onSetTask={onSetTask} />)
    expect(host.textContent).toContain('Unassigned legacy session')
    expect(host.textContent).toContain('Task: task-1 (Default)')
    expect(host.textContent).toContain('Task: task-2 (Manual)')
    expect(host.querySelector('[aria-label="Reassign task for Assigned checks"]')).not.toBeNull()
    act(() => host.querySelector<HTMLButtonElement>('[aria-label="Reassign task for Assigned checks"]')?.click())
    expect(onSetTask).toHaveBeenCalledWith('s2', 'replacement-task')
    act(() => host.querySelector<HTMLButtonElement>('[aria-label="Remove task from Assigned checks"]')?.click())
    expect(onSetTask).toHaveBeenCalledWith('s2', null)
    window.prompt = originalPrompt
  })
})
