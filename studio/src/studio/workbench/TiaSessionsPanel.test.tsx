// @vitest-environment happy-dom
import React, { act } from 'react'
import { createRoot } from 'react-dom/client'
import { afterEach, describe, expect, it, vi } from 'vitest'
import type * as api from '@/api/client'
import TiaSessionsPanel from './TiaSessionsPanel'

globalThis.IS_REACT_ACT_ENVIRONMENT = true

const sessions: api.SessionInfo[] = [
  { id: 17, mode: 'WithUI', projectPath: 'C:\\orca\\workspaces\\Demo\\app\\tia\\Demo.ap17', portalPath: 'C:\\Program Files\\Siemens\\Portal.exe' },
  { id: 23, mode: 'WithoutUI', projectPath: null, portalPath: null },
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

const render = (props: Partial<Parameters<typeof TiaSessionsPanel>[0]> = {}) => {
  const host = document.createElement('div')
  document.body.appendChild(host)
  const root = createRoot(host)
  const allProps = {
    sessions,
    current: detachedCurrent,
    busy: null,
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
  it('lists detected instances with pid, mode, and project path', () => {
    const { host, root } = render()

    expect(host.querySelector('[data-tia-sessions-panel]')).not.toBeNull()
    expect(host.querySelector('[data-tia-session="17"]')?.textContent).toContain('PID 17')
    expect(host.querySelector('[data-tia-session="17"]')?.textContent).toContain('Demo.ap17')
    expect(host.querySelector('[data-tia-session="23"]')?.textContent).toContain('WithoutUI')
    expect(host.textContent).toContain('Not attached')

    act(() => root.unmount())
  })

  it('shows the attached instance with a badge and a detach button', () => {
    const { host, root, props } = render({ current: attachedCurrent })

    expect(host.querySelector('[data-tia-current]')?.textContent).toContain('Attached to PID 17')
    expect(host.querySelector('[data-tia-session="17"]')?.textContent).toContain('Attached')
    expect(host.querySelector('[data-tia-attach="17"]')).toBeNull()
    expect(host.querySelector('[data-tia-attach="23"]')).not.toBeNull()

    click(host.querySelector('[data-tia-detach]'))
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
