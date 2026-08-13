// @vitest-environment happy-dom
import React, { act } from 'react'
import { createRoot } from 'react-dom/client'
import { afterEach, describe, expect, it, vi } from 'vitest'
import type { Workbench, WorkbenchRegistration } from '@/api/client'
import ArchiveProjectDialog from './ArchiveProjectDialog'

globalThis.IS_REACT_ACT_ENVIRONMENT = true

const workbench: Workbench = {
  schemaVersion: '1',
  workbenchId: 'workbench-1',
  name: 'Line 7',
  createdAt: '2026-08-13T00:00:00Z',
  rootPath: 'C:\\Automation\\Line7',
  repositoryPath: 'C:\\Automation\\Line7\\.git',
  engineeringProjectId: null,
  sourceProjectPath: null,
  worktrees: [],
}

const worktree: WorkbenchRegistration = {
  worktreeId: 'master-1',
  name: 'master',
  branch: 'master',
  relativePath: 'worktrees\\master',
}

const renderDialog = (overrides: Partial<Parameters<typeof ArchiveProjectDialog>[0]> = {}) => {
  const host = document.createElement('div')
  document.body.appendChild(host)
  const root = createRoot(host)
  const props = {
    workbench,
    worktree,
    busy: false,
    error: null,
    onClose: vi.fn(),
    onBrowseExportDirectory: vi.fn(() => Promise.resolve(null)),
    onArchive: vi.fn(() => Promise.resolve()),
    ...overrides,
  }
  act(() => root.render(<ArchiveProjectDialog {...props} />))
  return { host, root, props }
}

const setInputValue = (input: HTMLInputElement, value: string) => {
  const setter = Object.getOwnPropertyDescriptor(window.HTMLInputElement.prototype, 'value')!.set!
  setter.call(input, value)
  input.dispatchEvent(new window.Event('input', { bubbles: true }))
}

afterEach(() => {
  document.body.innerHTML = ''
})

describe('ArchiveProjectDialog', () => {
  it('defaults the archive name to the project name and local timestamp', () => {
    vi.useFakeTimers()
    vi.setSystemTime(new Date(2026, 7, 13, 11, 3, 0))
    try {
      const { host } = renderDialog()

      expect(host.querySelector<HTMLInputElement>('input[aria-label="Archive file name"]')?.value)
        .toBe('Line 7_202608131103.zap17')
    } finally {
      vi.useRealTimers()
    }
  })

  it('defaults the export directory to the workbench archive folder', () => {
    const { host } = renderDialog()

    expect(host.querySelector<HTMLInputElement>('input[aria-label="Export directory"]')?.value)
      .toBe('C:\\Automation\\Line7\\archive')
  })

  it('requires both an export directory and archive file name', () => {
    const { host } = renderDialog()
    const submit = host.querySelector('button[type="submit"]') as HTMLButtonElement

    act(() => setInputValue(host.querySelector('input[aria-label="Export directory"]')!, ''))
    expect(submit.disabled).toBe(true)
    act(() => setInputValue(host.querySelector('input[aria-label="Export directory"]')!, 'C:\\Exports'))
    expect(submit.disabled).toBe(false)
  })

  it('submits the selected export path, file name, and archive mode', async () => {
    const onArchive = vi.fn(() => Promise.resolve())
    const { host } = renderDialog({ onArchive })

    act(() => setInputValue(host.querySelector('input[aria-label="Export directory"]')!, 'C:\\Exports'))
    act(() => setInputValue(host.querySelector('input[aria-label="Archive file name"]')!, 'Line7.zap17'))
    act(() => {
      const select = host.querySelector('select[aria-label="Archive mode"]') as HTMLSelectElement
      select.value = 'none'
      select.dispatchEvent(new window.Event('change', { bubbles: true }))
    })
    await act(async () => (host.querySelector('button[type="submit"]') as HTMLButtonElement).click())

    expect(onArchive).toHaveBeenCalledWith({
      targetDirectory: 'C:\\Exports',
      archiveName: 'Line7.zap17',
      archivationMode: 'none',
    })
  })

  it('rejects a path in the archive file name field', () => {
    const { host } = renderDialog()
    act(() => setInputValue(host.querySelector('input[aria-label="Export directory"]')!, 'C:\\Exports'))
    act(() => setInputValue(host.querySelector('input[aria-label="Archive file name"]')!, 'C:\\Exports\\Line7.zap17'))

    expect(host.textContent).toContain('Enter a file name only')
    expect((host.querySelector('button[type="submit"]') as HTMLButtonElement).disabled).toBe(true)
  })

  it('fills the export directory from the system folder picker', async () => {
    const onBrowseExportDirectory = vi.fn(() => Promise.resolve('C:\\Exports'))
    const { host } = renderDialog({ onBrowseExportDirectory })

    await act(async () => {
      host.querySelector<HTMLButtonElement>('button[aria-label="Browse for export directory"]')?.click()
    })

    expect(onBrowseExportDirectory).toHaveBeenCalledTimes(1)
    expect(host.querySelector<HTMLInputElement>('input[aria-label="Export directory"]')?.value).toBe('C:\\Exports')
  })

  it('keeps the export directory field read-only so path selection goes through the explorer', () => {
    const { host } = renderDialog()

    expect(host.querySelector<HTMLInputElement>('input[aria-label="Export directory"]')?.readOnly).toBe(true)
  })
})
