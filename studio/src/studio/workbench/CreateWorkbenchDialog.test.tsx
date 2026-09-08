// @vitest-environment happy-dom
import React, { act } from 'react'
import { createRoot } from 'react-dom/client'
import { afterEach, describe, expect, it, vi } from 'vitest'
import type { OperationStatus, SessionInfo } from '@/api/client'
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
    onBrowseProjectFile: vi.fn(() => Promise.resolve(null)),
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

  it('shows visible progress while workbench creation is running', () => {
    const operationStatus: OperationStatus = {
      operationId: 'create-1',
      operationType: 'create-workbench',
      state: 'running',
      message: 'Exporting PLC source for PLC_1...',
      updatedAt: '2026-09-07T00:00:03Z',
      errorMessage: null,
      completedPhases: [
        {
          message: 'Initializing Git repository...',
          startedAt: '2026-09-07T00:00:00Z',
          completedAt: '2026-09-07T00:00:02Z',
          elapsedMilliseconds: 2000,
        },
        {
          message: 'Reading software checksum...',
          startedAt: '2026-09-07T00:00:02Z',
          completedAt: '2026-09-07T00:00:03Z',
          elapsedMilliseconds: 1000,
        },
        {
          message: 'Exporting block Early_OB1...',
          startedAt: '2026-09-07T00:00:03Z',
          completedAt: '2026-09-07T00:00:04Z',
          elapsedMilliseconds: 1000,
        },
        {
          message: 'Exporting block Latest_FB1...',
          startedAt: '2026-09-07T00:00:04Z',
          completedAt: '2026-09-07T00:00:05Z',
          elapsedMilliseconds: 1000,
        },
      ],
      currentPhase: {
        message: 'Exporting block Active_FC1...',
        startedAt: '2026-09-07T00:00:05Z',
        completedAt: null,
        elapsedMilliseconds: 1000,
      },
    }
    const { host } = renderDialog({ busy: true, operationStatus })

    expect(host.querySelector('[data-creation-progress]')?.textContent).toContain('Creating workbench project')
    expect(host.querySelector('input[placeholder="Line-7 commissioning"]')).toBeNull()
    expect(host.querySelector('[data-operation-timings]')?.textContent).toContain('Initializing Git repository...')
    expect(host.querySelector('[data-operation-timings]')?.textContent).toContain('2.0 s')
    const workflowStages = host.querySelector('[aria-label="Workflow stages"]')
    expect(workflowStages?.textContent?.indexOf('Reading software checksum...')).toBeLessThan(workflowStages?.textContent?.indexOf('Initializing Git repository...') ?? 0)
    const sourceExport = host.querySelector('[data-source-export-activity]')
    expect(sourceExport?.textContent).toContain('Exporting block Active_FC1...')
    expect(sourceExport?.textContent).toContain('Exporting block Latest_FB1...')
    expect(sourceExport?.querySelector('ol')?.className).toContain('scrollbar-sleek')
    expect(sourceExport?.className).toContain('flex-col')
    expect(sourceExport?.querySelector('ol')?.className).toContain('flex-1')
    expect(sourceExport?.querySelector('ol')?.className).not.toContain('max-h-[30vh]')
    expect(sourceExport?.textContent?.indexOf('Active_FC1')).toBeLessThan(sourceExport?.textContent?.indexOf('Latest_FB1') ?? 0)
    expect(sourceExport?.textContent?.indexOf('Latest_FB1')).toBeLessThan(sourceExport?.textContent?.indexOf('Early_OB1') ?? 0)
  })

  it('keeps exported block rows mounted when newer progress arrives', () => {
    const operationStatus: OperationStatus = {
      operationId: 'create-1',
      operationType: 'create-workbench',
      state: 'running',
      message: 'Exporting PLC source for PLC_1...',
      updatedAt: '2026-09-07T00:00:04Z',
      errorMessage: null,
      completedPhases: [
        {
          message: 'Exporting block Early_OB1...',
          startedAt: '2026-09-07T00:00:02Z',
          completedAt: '2026-09-07T00:00:03Z',
          elapsedMilliseconds: 1000,
        },
        {
          message: 'Exporting block Latest_FB1...',
          startedAt: '2026-09-07T00:00:03Z',
          completedAt: '2026-09-07T00:00:04Z',
          elapsedMilliseconds: 1000,
        },
      ],
      currentPhase: null,
    }
    const { host, root, props } = renderDialog({ busy: true, operationStatus })
    const rowFor = (name: string) => [...(host.querySelector('[data-source-export-activity]')?.querySelectorAll('li') ?? [])]
      .find(row => row.textContent?.includes(name))
    const latestBefore = rowFor('Latest_FB1')

    act(() => root.render(
      <CreateWorkbenchDialog
        {...props}
        operationStatus={{
          ...operationStatus,
          updatedAt: '2026-09-07T00:00:05Z',
          completedPhases: [
            ...operationStatus.completedPhases!,
            {
              message: 'Exporting block Newest_FC1...',
              startedAt: '2026-09-07T00:00:04Z',
              completedAt: '2026-09-07T00:00:05Z',
              elapsedMilliseconds: 1000,
            },
          ],
        }}
      />,
    ))

    expect(rowFor('Latest_FB1')).toBe(latestBefore)
  })

  it('renders source export activity in 100-row pages and loads the next page on scroll', () => {
    const completedPhases = Array.from({ length: 101 }, (_, index) => ({
      message: `Exporting UDT Type_${index}...`,
      startedAt: `2026-09-07T${String(Math.floor(index / 60)).padStart(2, '0')}:${String(index % 60).padStart(2, '0')}:00Z`,
      completedAt: `2026-09-07T${String(Math.floor(index / 60)).padStart(2, '0')}:${String(index % 60).padStart(2, '0')}:01Z`,
      elapsedMilliseconds: 1000,
    }))
    const operationStatus: OperationStatus = {
      operationId: 'create-1',
      operationType: 'create-workbench',
      state: 'running',
      message: 'Exporting PLC source for PLC_1...',
      updatedAt: '2026-09-07T00:02:00Z',
      errorMessage: null,
      completedPhases,
      currentPhase: null,
    }
    const { host } = renderDialog({ busy: true, operationStatus })
    const sourceExport = host.querySelector<HTMLElement>('[data-source-export-activity]')!
    const sourceList = sourceExport.querySelector<HTMLOListElement>('ol')!

    expect(sourceList.querySelectorAll('li')).toHaveLength(100)
    expect(sourceExport.textContent).toContain('Showing latest 100 of 101')
    expect(sourceExport.textContent).toContain('Exporting UDT Type_100...')
    expect(host.querySelector('[aria-label="Workflow stages"]')?.textContent).not.toContain('Type_100')

    Object.defineProperties(sourceList, {
      scrollTop: { configurable: true, value: 500, writable: true },
      clientHeight: { configurable: true, value: 100 },
      scrollHeight: { configurable: true, value: 600 },
    })
    act(() => sourceList.dispatchEvent(new window.Event('scroll', { bubbles: true })))

    expect(sourceList.querySelectorAll('li')).toHaveLength(101)
    expect(sourceExport.textContent).toContain('Showing all 101 source export items.')
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

  it('fills the project path from the native file picker', async () => {
    const onBrowseProjectFile = vi.fn(() => Promise.resolve('C:\\Projects\\Line.ap17'))
    const { host } = renderDialog({ onBrowseProjectFile })
    const fileModeButton = [...host.querySelectorAll<HTMLButtonElement>('button')]
      .find(button => button.textContent?.includes('Open project file'))!

    act(() => fileModeButton.click())
    await act(async () => {
      host.querySelector<HTMLButtonElement>('button[aria-label="Browse for TIA project file"]')?.click()
    })

    expect(onBrowseProjectFile).toHaveBeenCalledTimes(1)
    expect(host.querySelector<HTMLInputElement>('input[placeholder*="Line.ap17"]')?.value).toBe('C:\\Projects\\Line.ap17')
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
