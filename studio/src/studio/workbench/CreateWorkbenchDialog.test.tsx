// @vitest-environment happy-dom
import React, { act } from 'react'
import { createRoot } from 'react-dom/client'
import { afterEach, describe, expect, it, vi } from 'vitest'
import type { SessionInfo } from '@/api/client'
import CreateWorkbenchDialog from './CreateWorkbenchDialog'

globalThis.IS_REACT_ACT_ENVIRONMENT = true

const sessions: SessionInfo[] = [
  { id: 17, mode: 'Attached', projectPath: 'C:\\Projects\\Line.ap17' },
]

const render = (element: React.ReactNode) => {
  const host = document.createElement('div')
  document.body.appendChild(host)
  const root = createRoot(host)
  act(() => root.render(element))
  return { host, root }
}

const renderDialog = (overrides: Partial<Parameters<typeof CreateWorkbenchDialog>[0]> = {}) => {
  const props = {
    sessions,
    sandboxRoots: [] as string[],
    busy: false,
    operationStatus: null,
    onDismissOperation: vi.fn(),
    onRefreshSessions: vi.fn(() => Promise.resolve()),
    onClose: vi.fn(),
    onCreate: vi.fn(() => Promise.resolve()),
    ...overrides,
  }
  const rendered = render(<CreateWorkbenchDialog {...props} />)
  return { ...rendered, props }
}

const setInputValue = (input: HTMLInputElement, value: string) => {
  const setter = Object.getOwnPropertyDescriptor(window.HTMLInputElement.prototype, 'value')!.set!
  setter.call(input, value)
  input.dispatchEvent(new window.Event('input', { bubbles: true }))
}

afterEach(() => {
  document.body.innerHTML = ''
})

describe('CreateWorkbenchDialog', () => {
  it('refreshes the TIA session list from the refresh button', async () => {
    const onRefreshSessions = vi.fn(() => Promise.resolve())
    const { host } = renderDialog({ onRefreshSessions })

    await act(async () => {
      host.querySelector<HTMLButtonElement>('button[aria-label="Refresh TIA sessions"]')?.click()
    })

    expect(onRefreshSessions).toHaveBeenCalledTimes(1)
  })

  it('submits only the session id in attach mode', async () => {
    const onCreate = vi.fn(() => Promise.resolve())
    const { host } = renderDialog({ onCreate })
    const nameInput = host.querySelector<HTMLInputElement>('input[placeholder="Line-7 commissioning"]')!

    act(() => setInputValue(nameInput, 'Line 7'))
    const createButton = [...host.querySelectorAll<HTMLButtonElement>('button')]
      .find(button => button.textContent?.includes('Create workbench'))!
    await act(async () => createButton.click())

    expect(onCreate).toHaveBeenCalledWith({
      name: 'Line 7',
      rootPath: undefined,
      engineeringSessionId: 17,
    })
  })

  it('submits only the .ap17 project path in file mode', async () => {
    const onCreate = vi.fn(() => Promise.resolve())
    const { host } = renderDialog({ onCreate })
    const nameInput = host.querySelector<HTMLInputElement>('input[placeholder="Line-7 commissioning"]')!
    act(() => setInputValue(nameInput, 'Line 7'))
    const fileModeButton = [...host.querySelectorAll<HTMLButtonElement>('button')]
      .find(button => button.textContent?.includes('Open project file'))!
    act(() => fileModeButton.click())

    const createButton = () => [...host.querySelectorAll<HTMLButtonElement>('button')]
      .find(button => button.textContent?.includes('Create workbench'))!
    const fileInput = host.querySelector<HTMLInputElement>('input[placeholder*="Line.ap17"]')!
    act(() => setInputValue(fileInput, 'C:\\Projects\\Line.txt'))
    expect(createButton().disabled).toBe(true)
    expect(host.textContent).toContain('.ap17')

    act(() => setInputValue(fileInput, 'C:\\Projects\\Line.ap17'))
    expect(createButton().disabled).toBe(false)
    await act(async () => createButton().click())

    expect(onCreate).toHaveBeenCalledWith({
      name: 'Line 7',
      rootPath: undefined,
      engineeringProjectPath: 'C:\\Projects\\Line.ap17',
    })
  })

  it('warns when the project file is outside the sandbox whitelist', () => {
    const { host } = renderDialog({ sandboxRoots: ['C:\\Allowed\\'] })
    const fileModeButton = [...host.querySelectorAll<HTMLButtonElement>('button')]
      .find(button => button.textContent?.includes('Open project file'))!
    act(() => fileModeButton.click())
    const fileInput = host.querySelector<HTMLInputElement>('input[placeholder*="Line.ap17"]')!

    act(() => setInputValue(fileInput, 'C:\\Projects\\Line.ap17'))
    expect(host.textContent).toContain('outside the sandbox whitelist')

    act(() => setInputValue(fileInput, 'C:\\Allowed\\Line\\Line.ap17'))
    expect(host.textContent).not.toContain('outside the sandbox whitelist')
  })
})
