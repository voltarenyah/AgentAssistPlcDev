// @vitest-environment happy-dom
import React, { act } from 'react'
import { createRoot } from 'react-dom/client'
import { afterEach, describe, expect, it, vi } from 'vitest'
import type * as api from '@/api/client'
import TiaSessionsPanel, { formatSessionLabel, sessionModeLabel } from './TiaSessionsPanel'

globalThis.IS_REACT_ACT_ENVIRONMENT = true

const sessions: api.SessionInfo[] = [
  { id: 17, mode: 'WithUserInterface', projectPath: 'C:\\orca\\workspaces\\Demo\\app\\tia\\Demo.ap17', portalPath: 'C:\\Program Files\\Siemens\\Portal.exe' },
  { id: 23, mode: 'WithoutUserInterface', projectPath: null, portalPath: null },
]

const attachedCurrent: api.CurrentTiaSession = {
  attached: true,
  sessionId: 17,
  projectName: 'Demo',
  projectPath: 'C:\\orca\\workspaces\\Demo\\app\\tia\\Demo.ap17',
}

const detachedCurrent: api.CurrentTiaSession = {
  attached: false,
  sessionId: null,
  projectName: null,
  projectPath: null,
}

const resolveLabel = (session: api.SessionInfo) =>
  session.id === 17 ? { project: 'Demo', worktree: 'feature-x' } : null

const render = (props: Partial<Parameters<typeof TiaSessionsPanel>[0]> = {}) => {
  const host = document.createElement('div')
  document.body.appendChild(host)
  const root = createRoot(host)
  const allProps = {
    sessions,
    current: detachedCurrent,
    busy: null,
    resolveLabel,
    onRefresh: vi.fn(),
    onAttach: vi.fn(),
    onDetach: vi.fn(),
    onCloseSession: vi.fn(),
    onClose: vi.fn(),
    ...props,
  }
  act(() => root.render(<TiaSessionsPanel {...allProps} />))
  return { host, root, props: allProps }
}

const click = (element: Element | null) => {
  expect(element).not.toBeNull()
  act(() => { element!.dispatchEvent(new MouseEvent('click', { bubbles: true })) })
}

afterEach(() => {
  document.body.innerHTML = ''
})

describe('TiaSessionsPanel', () => {
  it('lists each instance once with pid, short mode label, and Project / worktree', () => {
    const { host, root } = render()

    expect(host.querySelector('[data-tia-sessions-panel]')).not.toBeNull()
    expect(host.querySelectorAll('[data-tia-session]')).toHaveLength(2)
    const row = host.querySelector('[data-tia-session="17"]')
    expect(row?.textContent).toContain('PID 17')
    expect(row?.textContent).toContain('UI')
    expect(row?.textContent).toContain('Demo / feature-x')
    expect(row?.textContent).not.toContain('C:\\orca')
    expect(host.querySelector('[data-tia-session="23"]')?.textContent).toContain('Headless')
    expect(host.querySelector('[data-tia-session="23"]')?.textContent).toContain('No project open')

    act(() => root.unmount())
  })

  it('marks the attached row with a badge and a filled detach button, no duplicate entry', () => {
    const { host, root, props } = render({ current: attachedCurrent })

    expect(host.querySelectorAll('[data-tia-session]')).toHaveLength(2)
    const attachedRow = host.querySelector('[data-tia-session="17"]')
    expect(attachedRow?.textContent).toContain('Attached')
    expect(attachedRow?.querySelector('[data-tia-detach]')).not.toBeNull()
    expect(host.querySelector('[data-tia-attach="17"]')).toBeNull()
    expect(host.querySelector('[data-tia-attach="23"]')).not.toBeNull()

    click(attachedRow?.querySelector('[data-tia-detach]') ?? null)
    expect(props.onDetach).toHaveBeenCalledTimes(1)

    act(() => root.unmount())
  })

  it('attaches a session from its row', () => {
    const { host, root, props } = render()

    click(host.querySelector('[data-tia-attach="23"]'))
    expect(props.onAttach).toHaveBeenCalledWith(23)

    act(() => root.unmount())
  })

  it('requires a confirmation before closing an instance', () => {
    const { host, root, props } = render()

    click(host.querySelector('[data-tia-close="17"]'))
    expect(props.onCloseSession).not.toHaveBeenCalled()

    click(host.querySelector('[data-tia-close-confirm="17"]'))
    expect(props.onCloseSession).toHaveBeenCalledWith(17)

    act(() => root.unmount())
  })

  it('cancelling the close confirmation keeps the instance', () => {
    const { host, root, props } = render()

    click(host.querySelector('[data-tia-close="23"]'))
    const keep = Array.from(host.querySelectorAll('button')).find(button => button.textContent === 'Keep')
    click(keep ?? null)
    expect(props.onCloseSession).not.toHaveBeenCalled()
    expect(host.querySelector('[data-tia-close-confirm="23"]')).toBeNull()

    act(() => root.unmount())
  })

  it('forwards refresh and close actions and shows an empty state', () => {
    const { host, root, props } = render({ sessions: [] })

    expect(host.textContent).toContain('No running TIA Portal instances')
    click(host.querySelector('[aria-label="Refresh TIA instances"]'))
    expect(props.onRefresh).toHaveBeenCalledTimes(1)
    click(host.querySelector('[aria-label="Close panel"]'))
    expect(props.onClose).toHaveBeenCalledTimes(1)

    act(() => root.unmount())
  })
})

describe('sessionModeLabel', () => {
  it('maps verbose TIA modes to short labels', () => {
    expect(sessionModeLabel('WithUserInterface')).toBe('UI')
    expect(sessionModeLabel('WithoutUserInterface')).toBe('Headless')
    expect(sessionModeLabel('WithUI')).toBe('UI')
    expect(sessionModeLabel('WithoutUI')).toBe('Headless')
    expect(sessionModeLabel('Unknown')).toBe('Unknown')
  })
})

describe('formatSessionLabel', () => {
  it('formats Project / worktree with fallbacks', () => {
    expect(formatSessionLabel({ project: 'Demo', worktree: 'feature-x' })).toBe('Demo / feature-x')
    expect(formatSessionLabel({ project: 'Demo', worktree: null })).toBe('Demo')
    expect(formatSessionLabel({ project: null, worktree: null })).toBe('No project open')
    expect(formatSessionLabel(null)).toBe('No project open')
  })
})
