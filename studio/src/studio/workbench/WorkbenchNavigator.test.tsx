// @vitest-environment happy-dom
import React, { act } from 'react'
import { createRoot } from 'react-dom/client'
import { afterEach, describe, expect, it, vi } from 'vitest'
import type * as api from '@/api/client'
import WorkbenchNavigator from './WorkbenchNavigator'

globalThis.IS_REACT_ACT_ENVIRONMENT = true

const workbenches: api.Workbench[] = [
  {
    schemaVersion: '1.0', workbenchId: 'wb-direct', name: 'Direct project', createdAt: '2026-09-09T00:00:00Z', rootPath: 'C:/direct', repositoryPath: 'C:/direct/repo', engineeringProjectId: null, sourceProjectPath: null,
    worktrees: [
      { worktreeId: 'wt-descendant', name: 'descendant match', branch: 'feature/descendant', relativePath: 'worktrees/descendant' },
      { worktreeId: 'wt-unmatched', name: 'unmatched worktree', branch: 'feature/unmatched', relativePath: 'worktrees/unmatched' },
    ],
  },
  {
    schemaVersion: '1.0', workbenchId: 'wb-parent', name: 'Parent-only project', createdAt: '2026-09-09T00:00:00Z', rootPath: 'C:/parent', repositoryPath: 'C:/parent/repo', engineeringProjectId: null, sourceProjectPath: null,
    worktrees: [{ worktreeId: 'wt-unavailable', name: 'unavailable match', branch: 'feature/unavailable', relativePath: 'worktrees/unavailable' }],
  },
]

const callbacks = {
  onCreateWorkbench: () => {}, onCreateWorktree: () => {}, onOpenWorkbench: () => {}, onOpenWorktree: () => {}, onInspectWorkbench: () => {}, onInspectWorktree: () => {}, onArchiveWorktree: () => {}, onRefresh: () => {}, onShowHome: () => {}, onSelectWorkbench: () => {}, onSelectWorktree: () => {}, onSelectDevice: () => {}, onSelectHardware: () => {}, onReloadHardware: () => {}, onCompareHardware: () => {}, onDeleteWorkbench: () => {}, onDeleteWorktree: () => {}, onMergeWorktree: () => {}, onOpenDevice: () => {}, onUpgradeDevice: () => {}, onInspectDevice: () => {}, onCompareDevice: () => {}, onRebuildDevice: () => {}, onUpdateKnowledge: () => {}, onRebuildKnowledge: () => {},
}

const renderNavigator = async (filteredResults: api.WorkbenchTagSearchResults | null, filterActive = true) => {
  const host = document.createElement('div')
  document.body.appendChild(host)
  const root = createRoot(host)
  await act(async () => root.render(
    <WorkbenchNavigator
      workbenches={workbenches}
      devicesByWorktree={{}}
      selection={{ workbenchId: null, worktreeId: null, deviceId: null }}
      viewKind="project"
      knowledgeState={{}}
      loading={false}
      filterActive={filterActive}
      filteredResults={filteredResults}
      {...callbacks}
    />,
  ))
  return { host, root }
}

afterEach(() => { document.body.innerHTML = '' })

describe('WorkbenchNavigator tag projection', () => {
  it('renders task children as a compact outline without a Tasks label', async () => {
    const { host, root } = await renderNavigator(null, false)
    await act(async () => root.render(
      <WorkbenchNavigator
        workbenches={workbenches}
        devicesByWorktree={{}}
        tasksByWorktree={{
          'wb-direct:wt-descendant': [{
            taskId: 'task-1', workbenchId: 'wb-direct', scope: 'worktree', worktreeId: 'wt-descendant',
            title: 'Review motor interlock', type: 'feature', status: 'inProgress', priority: 0,
            intent: 'Review', expectedResult: 'Verified', description: null, createdUtc: '', updatedUtc: '', deviceId: 'plc-1',
          }],
        }}
        activeTaskId="task-1"
        selection={{ workbenchId: 'wb-direct', worktreeId: 'wt-descendant', deviceId: 'plc-other' }}
        viewKind="device"
        knowledgeState={{}}
        loading={false}
        filterActive={false}
        filteredResults={null}
        {...callbacks}
      />,
    ))
    const worktreeName = Array.from(host.querySelectorAll('span')).find(node => node.textContent === 'descendant match')
    await act(async () => (worktreeName?.parentElement as HTMLElement).click())

    expect(host.textContent).toContain('Review motor interlock')
    expect(host.textContent).toContain('PROJECTS')
    expect(host.textContent).not.toContain('Tasks')
    expect(host.querySelector('[data-task-status="inProgress"]')).toBeTruthy()
    expect(host.querySelector('[data-task-status="inProgress"]')?.className).toContain('bg-emerald-500')
    const task = host.querySelector('button[aria-label="Open task Review motor interlock"]')
    expect(task?.getAttribute('aria-current')).toBe('page')
    expect(task?.getAttribute('data-task-selected')).toBe('true')
    expect(host.querySelector('button[aria-label="Project actions Direct project"]')).toBeTruthy()
    expect(host.querySelector('button[aria-label="Worktree actions descendant match"]')).toBeTruthy()
    expect(host.querySelector('[aria-label="Project available"]')).toBeNull()
    expect(host.querySelector('[aria-label="Worktree status"]')).toBeNull()
    expect(host.querySelector('[data-lucide="ellipsis"]')).toBeNull()

    await act(async () => root.unmount())
  })

  it('offers an icon-only Home action beside the workbench title', async () => {
    const onShowHome = vi.fn()
    const { host, root } = await renderNavigator(null, false)
    await act(async () => root.render(
      <WorkbenchNavigator
        workbenches={workbenches}
        devicesByWorktree={{}}
        selection={{ workbenchId: 'wb-direct', worktreeId: null, deviceId: null }}
        viewKind="project"
        knowledgeState={{}}
        loading={false}
        filterActive={false}
        filteredResults={null}
        {...callbacks}
        onShowHome={onShowHome}
      />,
    ))

    const home = host.querySelector('button[aria-label="Go to all projects"]')
    expect(home).toBeTruthy()
    expect(home?.getAttribute('data-variant')).toBe('ghost')
    expect(home?.getAttribute('data-size')).toBe('icon-sm')
    await act(async () => (home as HTMLButtonElement).click())
    expect(onShowHome).toHaveBeenCalledOnce()
    await act(async () => root.unmount())
  })

  it('renders a one-tag descendant match and its parent project from the server result', async () => {
    const { host, root } = await renderNavigator({
      workbenches: [],
      worktrees: [{ entityType: 'worktree', entityId: 'wt-descendant', workbenchId: 'wb-direct', direct: ['press'], effective: ['press'], available: true }],
    })

    expect(host.textContent).toContain('Direct project')
    expect(host.textContent).toContain('descendant match')
    expect(host.textContent).not.toContain('unmatched worktree')
    await act(async () => root.unmount())
  })

  it('renders only the server AND-result worktree while retaining parent-only context and availability', async () => {
    const { host, root } = await renderNavigator({
      workbenches: [],
      worktrees: [{ entityType: 'worktree', entityId: 'wt-unavailable', workbenchId: 'wb-parent', direct: ['press', 'commission'], effective: ['press', 'commission'], available: false }],
    })

    expect(host.textContent).toContain('Parent-only project')
    expect(host.textContent).toContain('unavailable match')
    expect(host.textContent).toContain('Unavailable')
    expect(host.textContent).not.toContain('descendant match')
    await act(async () => root.unmount())
  })

  it('restores the unfiltered navigator when the final filter is cleared', async () => {
    const result = {
      workbenches: [],
      worktrees: [{ entityType: 'worktree' as const, entityId: 'wt-descendant', workbenchId: 'wb-direct', direct: ['press'], effective: ['press'], available: true }],
    }
    const { host, root } = await renderNavigator(result)
    expect(host.textContent).not.toContain('unmatched worktree')

    await act(async () => root.render(
      <WorkbenchNavigator
        workbenches={workbenches}
        devicesByWorktree={{}}
        selection={{ workbenchId: 'wb-direct', worktreeId: null, deviceId: null }}
        viewKind="project"
        knowledgeState={{}}
        loading={false}
        filterActive={false}
        filteredResults={null}
        {...callbacks}
      />,
    ))

    expect(host.textContent).toContain('unmatched worktree')
    await act(async () => root.unmount())
  })

  it('keeps previously expanded projects open when another project is selected', async () => {
    const { host, root } = await renderNavigator(null, false)
    const directName = Array.from(host.querySelectorAll('span')).find(node => node.textContent === 'Direct project')
    const directRow = directName?.parentElement
    expect(directName).toBeTruthy()
    expect(directRow).toBeTruthy()

    await act(async () => (directRow as HTMLElement).click())
    expect(host.textContent).toContain('descendant match')

    const parentName = Array.from(host.querySelectorAll('span')).find(node => node.textContent === 'Parent-only project')
    const parentRow = parentName?.parentElement
    expect(parentRow).toBeTruthy()
    await act(async () => (parentRow as HTMLElement).click())

    expect(host.textContent).toContain('descendant match')
    expect(host.textContent).toContain('unavailable match')

    await act(async () => (parentRow as HTMLElement).click())
    expect(host.textContent).toContain('descendant match')
    expect(host.textContent).not.toContain('unavailable match')
    await act(async () => root.unmount())
  })
})
