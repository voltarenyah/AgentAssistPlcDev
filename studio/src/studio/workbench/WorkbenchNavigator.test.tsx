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

const tasksByWorktree: Record<string, api.EngineeringTask[]> = {
  'wb-direct:wt-descendant': [{
    taskId: 'task-1', workbenchId: 'wb-direct', scope: 'worktree', worktreeId: 'wt-descendant',
    title: 'Review motor interlock', type: 'feature', status: 'inProgress', priority: 0,
    intent: 'Review', expectedResult: 'Verified', description: null, createdUtc: '', updatedUtc: '', deviceId: 'plc-1',
  }],
}

const callbacks = {
  onCreateWorkbench: () => {}, onCreateWorktree: () => {}, onOpenWorkbench: () => {}, onOpenWorktree: () => {}, onInspectWorkbench: () => {}, onInspectWorktree: () => {}, onArchiveWorktree: () => {}, onRefresh: () => {}, onShowHome: () => {}, onSelectWorkbench: () => {}, onSelectWorktree: () => {}, onSelectDevice: () => {}, onSelectHardware: () => {}, onReloadHardware: () => {}, onCompareHardware: () => {}, onDeleteWorkbench: () => {}, onDeleteWorktree: () => {}, onMergeWorktree: () => {}, onOpenDevice: () => {}, onUpgradeDevice: () => {}, onInspectDevice: () => {}, onCompareDevice: () => {}, onRebuildDevice: () => {}, onUpdateKnowledge: () => {}, onRebuildKnowledge: () => {},
}

type NavigatorProps = React.ComponentProps<typeof WorkbenchNavigator>

const navigatorProps = (overrides: Partial<NavigatorProps> = {}): NavigatorProps => ({
  workbenches,
  devicesByWorktree: {},
  selection: { workbenchId: null, worktreeId: null, deviceId: null },
  viewKind: 'project',
  knowledgeState: {},
  loading: false,
  filterActive: false,
  filteredResults: null,
  ...callbacks,
  ...overrides,
})

const renderNavigator = async (
  filteredResults: api.WorkbenchTagSearchResults | null,
  filterActive = true,
  overrides: Partial<NavigatorProps> = {},
) => {
  const host = document.createElement('div')
  document.body.appendChild(host)
  const root = createRoot(host)
  await act(async () => root.render(
    <WorkbenchNavigator {...navigatorProps({ filteredResults, filterActive, ...overrides })} />,
  ))
  return { host, root }
}

const sectionIds = (host: HTMLElement) => Array.from(host.querySelectorAll('[data-navigator-section]'))
  .map(node => node.getAttribute('data-navigator-section'))

const section = (host: HTMLElement, id: string) => host.querySelector(`[data-navigator-section="${id}"]`) as HTMLElement

const sectionHeader = (host: HTMLElement, id: string) =>
  host.querySelector(`[data-navigator-section="${id}"] button[aria-expanded]`) as HTMLButtonElement

const sectionBody = (host: HTMLElement, id: string) => host.querySelector(`#navigator-section-${id}`) as HTMLElement

afterEach(() => { document.body.innerHTML = '' })

describe('WorkbenchNavigator tag projection', () => {
  it('renders task children as a compact outline without a Tasks label', async () => {
    const { host, root } = await renderNavigator(null, false)
    await act(async () => root.render(
      <WorkbenchNavigator {...navigatorProps({
        tasksByWorktree,
        activeTaskId: 'task-1',
        selection: { workbenchId: 'wb-direct', worktreeId: 'wt-descendant', deviceId: 'plc-other' },
        viewKind: 'device',
      })} />,
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
    expect(task?.getAttribute('data-task-type')).toBe('feature')
    expect(host.querySelector('button[aria-label="Task actions Review motor interlock"]')).toBeTruthy()
    expect(host.querySelector('button[aria-label="Project actions Direct project"]')).toBeTruthy()
    expect(host.querySelector('button[aria-label="Worktree actions descendant match"]')).toBeTruthy()
    expect(host.querySelector('[aria-label="Project available"]')).toBeNull()
    expect(host.querySelector('[aria-label="Worktree status"]')).toBeNull()
    expect(host.querySelector('[data-lucide="ellipsis"]')).toBeNull()

    await act(async () => root.unmount())
  })

  it('offers an icon-only Home action beside the workbench title', async () => {
    const onShowHome = vi.fn()
    const { host, root } = await renderNavigator(null, false, {
      selection: { workbenchId: 'wb-direct', worktreeId: null, deviceId: null },
      onShowHome,
    })

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
      <WorkbenchNavigator {...navigatorProps({
        selection: { workbenchId: 'wb-direct', worktreeId: null, deviceId: null },
      })} />,
    ))

    expect(host.textContent).toContain('unmatched worktree')
    await act(async () => root.unmount())
  })

  it('shows only PROJECTS and WORKTREE while the tag filter is active and brings the task rows back when it clears (AC-006)', async () => {
    const result = {
      workbenches: [] as api.WorkbenchTagSearchResult[],
      worktrees: [{ entityType: 'worktree' as const, entityId: 'wt-descendant', workbenchId: 'wb-direct', direct: ['press'], effective: ['press'], available: true }],
    }
    const selection = { workbenchId: 'wb-direct', worktreeId: 'wt-descendant', deviceId: null }
    const { host, root } = await renderNavigator(result, true, { selection, tasksByWorktree })

    expect(sectionIds(host)).toEqual(['projects', 'worktree'])
    expect(section(host, 'worktree').textContent).toContain('descendant match')
    expect(host.textContent).not.toContain('Review motor interlock')

    await act(async () => root.render(
      <WorkbenchNavigator {...navigatorProps({
        filterActive: false,
        filteredResults: null,
        selection,
        tasksByWorktree,
      })} />,
    ))

    expect(sectionIds(host)).toEqual(['projects', 'worktree'])
    expect(host.textContent).toContain('Review motor interlock')
    await act(async () => root.unmount())
  })
})

describe('WorkbenchNavigator section cascade', () => {
  it('lists every workbench as a flat PROJECTS row and creates a workbench from the section header (AC-001)', async () => {
    const onCreateWorkbench = vi.fn()
    const { host, root } = await renderNavigator(null, false, {
      selection: { workbenchId: 'wb-parent', worktreeId: null, deviceId: null },
      onCreateWorkbench,
    })

    const projects = section(host, 'projects')
    expect(projects).toBeTruthy()
    expect(projects.textContent).toContain('Direct project')
    expect(projects.textContent).toContain('Parent-only project')
    // A workbench row is flat: the selected workbench's worktree lives in WORKTREE, never under it.
    expect(projects.textContent).not.toContain('unavailable match')

    const current = Array.from(projects.querySelectorAll('[aria-current]'))
    expect(current).toHaveLength(1)
    expect(current[0].textContent).toContain('Parent-only project')

    const create = projects.querySelector('button[aria-label="Create workbench"]') as HTMLButtonElement
    expect(create).toBeTruthy()
    await act(async () => create.click())
    expect(onCreateWorkbench).toHaveBeenCalledOnce()
    await act(async () => root.unmount())
  })

  it('hides WORKTREE until a workbench is selected (AC-002)', async () => {
    const { host, root } = await renderNavigator(null, false)
    expect(sectionIds(host)).toEqual(['projects'])

    await act(async () => root.render(
      <WorkbenchNavigator {...navigatorProps({
        selection: { workbenchId: 'wb-direct', worktreeId: null, deviceId: null },
      })} />,
    ))
    expect(sectionIds(host)).toEqual(['projects', 'worktree'])
    await act(async () => root.unmount())
  })

  it('derives WORKTREE from the selected workbench only and creates a worktree from its header (AC-002)', async () => {
    const onCreateWorktree = vi.fn()
    const { host, root } = await renderNavigator(null, false, {
      selection: { workbenchId: 'wb-direct', worktreeId: null, deviceId: null },
      onCreateWorktree,
    })

    expect(section(host, 'worktree').textContent).toContain('descendant match')
    expect(section(host, 'worktree').textContent).toContain('unmatched worktree')
    expect(section(host, 'worktree').textContent).not.toContain('unavailable match')

    await act(async () => root.render(
      <WorkbenchNavigator {...navigatorProps({
        selection: { workbenchId: 'wb-parent', worktreeId: null, deviceId: null },
        onCreateWorktree,
      })} />,
    ))

    expect(section(host, 'worktree').textContent).toContain('unavailable match')
    expect(section(host, 'worktree').textContent).not.toContain('descendant match')
    expect(section(host, 'worktree').textContent).not.toContain('unmatched worktree')

    const create = section(host, 'worktree')
      .querySelector('button[aria-label="Create worktree for Parent-only project"]') as HTMLButtonElement
    expect(create).toBeTruthy()
    await act(async () => create.click())
    expect(onCreateWorktree).toHaveBeenCalledWith(workbenches[1])
    await act(async () => root.unmount())
  })

  it('collapses only the activated section and keeps its body addressable (AC-007)', async () => {
    const { host, root } = await renderNavigator(null, false, {
      selection: { workbenchId: 'wb-direct', worktreeId: null, deviceId: null },
    })

    const projectsHeader = sectionHeader(host, 'projects')
    const worktreeHeader = sectionHeader(host, 'worktree')
    const projectsBody = sectionBody(host, 'projects')
    const worktreeBody = sectionBody(host, 'worktree')

    expect(projectsHeader.getAttribute('aria-expanded')).toBe('true')
    expect(projectsHeader.getAttribute('aria-controls')).toBe(projectsBody.id)
    expect(worktreeHeader.getAttribute('aria-expanded')).toBe('true')
    expect(projectsBody.hasAttribute('hidden')).toBe(false)
    expect(worktreeBody.hasAttribute('hidden')).toBe(false)

    // The header must sit outside the body's scroll region, otherwise a long section could
    // scroll its own header out of view. Scrolling a non-ancestor can never move it.
    expect(projectsBody.contains(projectsHeader)).toBe(false)
    expect(worktreeBody.contains(worktreeHeader)).toBe(false)

    await act(async () => projectsHeader.click())

    expect(projectsHeader.getAttribute('aria-expanded')).toBe('false')
    expect(projectsBody.hasAttribute('hidden')).toBe(true)
    expect(worktreeHeader.getAttribute('aria-expanded')).toBe('true')
    expect(worktreeBody.hasAttribute('hidden')).toBe(false)

    await act(async () => projectsHeader.click())

    expect(projectsHeader.getAttribute('aria-expanded')).toBe('true')
    expect(projectsBody.hasAttribute('hidden')).toBe(false)
    expect(worktreeHeader.getAttribute('aria-expanded')).toBe('true')
    await act(async () => root.unmount())
  })
})
