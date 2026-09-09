// @vitest-environment happy-dom
import React, { act } from 'react'
import { createRoot } from 'react-dom/client'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import * as api from '@/api/client'
import MainStudio from './MainStudio'

globalThis.IS_REACT_ACT_ENVIRONMENT = true

const workbenches: api.Workbench[] = [
  {
    schemaVersion: '1.0', workbenchId: 'wb-parent', name: 'Parent project', createdAt: '2026-09-09T00:00:00Z', rootPath: 'C:/parent', repositoryPath: 'C:/parent/repo', engineeringProjectId: null, sourceProjectPath: null,
    worktrees: [
      { worktreeId: 'wt-match', name: 'matching child', branch: 'feature/match', relativePath: 'worktrees/match' },
      { worktreeId: 'wt-other', name: 'other child', branch: 'feature/other', relativePath: 'worktrees/other' },
    ],
  },
]

vi.mock('@/api/client', async importOriginal => {
  const actual = await importOriginal<typeof import('@/api/client')>()
  return {
    ...actual,
    listWorkbenches: vi.fn(async () => workbenches),
    getTagTaxonomy: vi.fn(async () => ({ nodes: [
      { tagId: 'machine', parentTagId: null, name: 'Machine', normalizedName: 'machine' },
      { tagId: 'press', parentTagId: 'machine', name: 'Press', normalizedName: 'press' },
      { tagId: 'state', parentTagId: null, name: 'State', normalizedName: 'state' },
    ] })),
    searchWorkbenches: vi.fn(async () => ({
      workbenches: [],
      worktrees: [{ entityType: 'worktree', entityId: 'wt-match', workbenchId: 'wb-parent', direct: ['press'], effective: ['press'], available: true }],
    })),
    selectWorkbench: vi.fn(async () => ({})),
    getKeyStatus: vi.fn(async () => ({ configured: false })),
    getSessions: vi.fn(async () => []),
  }
})

vi.mock('flexlayout-react', async () => await import('@/test/flexLayoutMock'))

const setInputValue = async (input: HTMLInputElement, value: string) => {
  const setter = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value')!.set!
  await act(async () => {
    setter.call(input, value)
    input.dispatchEvent(new Event('input', { bubbles: true }))
  })
}

const deferred = <T,>() => {
  let resolve!: (value: T) => void
  const promise = new Promise<T>(resolvePromise => { resolve = resolvePromise })
  return { promise, resolve }
}

afterEach(() => { document.body.innerHTML = '' })
beforeEach(() => { vi.clearAllMocks() })

describe('MainStudio tag navigator filter', () => {
  it('keeps the filter state server-backed and projects only the returned matching worktree', async () => {
    const host = document.createElement('div')
    document.body.appendChild(host)
    const root = createRoot(host)
    await act(async () => root.render(<MainStudio />))
    await act(async () => {})

    await act(async () => (host.querySelector('button[aria-label="Filter tags"]') as HTMLButtonElement).click())
    const input = document.body.querySelector('input[aria-label="Search filter tags"]') as HTMLInputElement
    await setInputValue(input, 'Machine/Press')
    await act(async () => {
      input.dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowDown', bubbles: true }))
      input.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true }))
    })
    await act(async () => {})

    expect(api.searchWorkbenches).toHaveBeenCalledWith(['press'])
    expect(host.textContent).toContain('Parent project')
    expect(host.textContent).toContain('matching child')
    expect(host.textContent).not.toContain('other child')
    await act(async () => root.unmount())
  })

  it('clears the previous projection while a changed non-empty filter search is pending', async () => {
    const firstSearch = deferred<api.WorkbenchTagSearchResults>()
    const replacementSearch = deferred<api.WorkbenchTagSearchResults>()
    vi.mocked(api.searchWorkbenches)
      .mockImplementationOnce(() => firstSearch.promise)
      .mockImplementationOnce(() => replacementSearch.promise)

    const host = document.createElement('div')
    document.body.appendChild(host)
    const root = createRoot(host)
    await act(async () => root.render(<MainStudio />))
    await act(async () => {})

    await act(async () => (host.querySelector('button[aria-label="Filter tags"]') as HTMLButtonElement).click())
    let input = document.body.querySelector('input[aria-label="Search filter tags"]') as HTMLInputElement
    await setInputValue(input, 'Machine/Press')
    await act(async () => {
      input.dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowDown', bubbles: true }))
      input.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true }))
    })
    await act(async () => firstSearch.resolve({
      workbenches: [],
      worktrees: [{ entityType: 'worktree', entityId: 'wt-match', workbenchId: 'wb-parent', direct: ['press'], effective: ['press'], available: true }],
    }))
    expect(host.textContent).toContain('matching child')

    await act(async () => (host.querySelector('button[aria-label="Filter tags"]') as HTMLButtonElement).click())
    input = document.body.querySelector('input[aria-label="Search filter tags"]') as HTMLInputElement
    await setInputValue(input, 'State')
    await act(async () => {
      input.dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowDown', bubbles: true }))
      input.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true }))
    })

    expect(api.searchWorkbenches).toHaveBeenLastCalledWith(['press', 'state'])
    expect(host.textContent).toContain('Filtering projects and worktrees…')
    expect(host.textContent).not.toContain('matching child')

    await act(async () => replacementSearch.resolve({
      workbenches: [],
      worktrees: [{ entityType: 'worktree', entityId: 'wt-other', workbenchId: 'wb-parent', direct: ['press', 'state'], effective: ['press', 'state'], available: true }],
    }))
    expect(host.textContent).toContain('other child')
    expect(host.textContent).not.toContain('matching child')
    await act(async () => root.unmount())
  })
})
