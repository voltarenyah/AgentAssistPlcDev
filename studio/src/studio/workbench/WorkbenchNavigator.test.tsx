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

const devicesByWorktree: Record<string, api.DeviceSummary[]> = {
  'wb-direct:wt-descendant': [{ deviceId: 'plc-1', plcName: 'Main PLC' }],
}

const graphTask = (overrides: Partial<api.EngineeringTask> & { taskId: string; title: string }): api.EngineeringTask => ({
  workbenchId: 'wb-direct', scope: 'worktree', worktreeId: 'wt-descendant', type: 'feature', status: 'todo',
  priority: 0, intent: 'Intent', expectedResult: 'Result', description: null, createdUtc: '', updatedUtc: '',
  ...overrides,
})

/** One task per target, including the untargeted row only a pre-target-model worktree can hold. */
const targetTasks: Record<string, api.EngineeringTask[]> = {
  'wb-direct:wt-descendant': [
    graphTask({ taskId: 'task-device', title: 'Device task', deviceId: 'plc-1', targetKind: 'device' }),
    graphTask({ taskId: 'task-hardware', title: 'Hardware task', deviceId: null, targetKind: 'hardware' }),
    graphTask({ taskId: 'task-unbound', title: 'Unbound task', deviceId: null }),
  ],
}

const conversation = (overrides: Partial<api.ChatSessionInfo> & { sessionId: string }): api.ChatSessionInfo => ({
  title: null, createdAt: '2026-10-01T00:00:00Z', updatedAt: '2026-10-01T00:00:00Z',
  messageCount: 2, turnCount: 1, firstUserMessage: null, taskId: null,
  ...overrides,
})

/** One relation of a conversation's set, as the server projects it onto a session list row. */
const relation = (taskId: string, overrides: Partial<api.SessionTaskRelation> = {}): api.SessionTaskRelation => ({
  taskId, edgeId: `edge-${taskId}`, provenance: 'manual', isPrimary: false, ...overrides,
})

/** One conversation related to the device task, one related to nothing, as the live data has both. */
const sessionsByWorktree: Record<string, api.ChatSessionInfo[]> = {
  'wb-direct:wt-descendant': [
    conversation({
      sessionId: 'session-bound', title: 'Interlock review', taskId: 'task-device', deviceId: 'plc-1',
      taskProvenance: 'manual', taskRelations: [relation('task-device', { isPrimary: true })],
    }),
    conversation({ sessionId: 'session-unbound', title: 'Ad-hoc question', taskId: null, deviceId: 'plc-1' }),
  ],
}

const callbacks = {
  onCreateWorkbench: () => {}, onCreateWorktree: () => {}, onOpenWorkbench: () => {}, onOpenWorktree: () => {}, onInspectWorkbench: () => {}, onInspectWorktree: () => {}, onArchiveWorktree: () => {}, onRefresh: () => {}, onShowHome: () => {}, onSelectWorkbench: () => {}, onSelectWorktree: () => {}, onSelectDevice: () => {}, onSelectHardware: () => {}, onReloadHardware: () => {}, onCompareHardware: () => {}, onDeleteWorkbench: () => {}, onDeleteWorktree: () => {}, onMergeWorktree: () => {}, onOpenDevice: () => {}, onUpgradeDevice: () => {}, onInspectDevice: () => {}, onCompareDevice: () => {}, onRebuildDevice: () => {}, onUpdateKnowledge: () => {}, onRebuildKnowledge: () => {},
}

type NavigatorProps = React.ComponentProps<typeof WorkbenchNavigator>

const navigatorProps = (overrides: Partial<NavigatorProps> = {}): NavigatorProps => ({
  workbenches,
  devicesByWorktree: {},
  selection: { workbenchId: null, worktreeId: null, deviceId: null },
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

/** One group of the `SESSIONS` section, by the task it names or `unbound` for the task-less one. */
const sessionGroup = (host: HTMLElement, key: string) =>
  section(host, 'sessions').querySelector(`[data-session-group="${key}"]`) as HTMLElement

/** The sections whose bodies are showing, in the order the cascade puts them in. */
const expandedIds = (host: HTMLElement) => sectionIds(host)
  .filter(id => sectionHeader(host, id).getAttribute('aria-expanded') === 'true')

/**
 * Opens every folded section, so a case about something other than the accordion — the sizing and
 * separator mechanics — starts from the state it is about rather than from whatever the selected scope
 * folded. The accordion itself is covered on its own below.
 */
const expandAll = async (host: HTMLElement) => {
  for (const id of sectionIds(host)) {
    const header = sectionHeader(host, id)
    if (header.getAttribute('aria-expanded') === 'false') await act(async () => header.click())
  }
}

afterEach(() => { document.body.innerHTML = '' })

describe('WorkbenchNavigator tag projection', () => {
  it('renders the selected target tasks as a compact outline in the TASKS section', async () => {
    const { host, root } = await renderNavigator(null, false, {
      tasksByWorktree,
      devicesByWorktree,
      activeTaskId: 'task-1',
      selection: { workbenchId: 'wb-direct', worktreeId: 'wt-descendant', deviceId: 'plc-1', targetKind: 'device' },
    })

    expect(host.textContent).toContain('Review motor interlock')
    expect(host.textContent).toContain('PROJECTS')
    // A task row carries its type icon and title, and no status dot: the navigator's rows stay uniform.
    const task = host.querySelector('button[aria-label="Open task Review motor interlock"]')
    expect(task?.getAttribute('aria-current')).toBe('page')
    expect(task?.getAttribute('data-task-selected')).toBe('true')
    expect(task?.getAttribute('data-task-type')).toBe('feature')
    expect(host.querySelector('[data-task-status]')).toBeNull()
    // The task lives in TASKS, never under its worktree row.
    expect(section(host, 'tasks').contains(task)).toBe(true)
    expect(section(host, 'worktree').contains(task)).toBe(false)
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

  it('shows only PROJECTS and WORKTREE while the tag filter is active and brings the deeper sections back when it clears (AC-006)', async () => {
    const result = {
      workbenches: [] as api.WorkbenchTagSearchResult[],
      worktrees: [{ entityType: 'worktree' as const, entityId: 'wt-descendant', workbenchId: 'wb-direct', direct: ['press'], effective: ['press'], available: true }],
    }
    const selection = { workbenchId: 'wb-direct', worktreeId: 'wt-descendant', deviceId: 'plc-1', targetKind: 'device' as const }
    const overrides = { selection, tasksByWorktree, devicesByWorktree }
    const { host, root } = await renderNavigator(result, true, overrides)

    expect(sectionIds(host)).toEqual(['projects', 'worktree'])
    expect(section(host, 'worktree').textContent).toContain('descendant match')
    expect(host.textContent).not.toContain('Review motor interlock')

    await act(async () => root.render(
      <WorkbenchNavigator {...navigatorProps({
        filterActive: false,
        filteredResults: null,
        ...overrides,
      })} />,
    ))

    expect(sectionIds(host)).toEqual(['projects', 'worktree', 'device', 'tasks', 'sessions'])
    expect(host.textContent).toContain('Review motor interlock')
    await act(async () => root.unmount())
  })
})

describe('WorkbenchNavigator target cascade', () => {
  const deviceSelection = { workbenchId: 'wb-direct', worktreeId: 'wt-descendant', deviceId: 'plc-1', targetKind: 'device' as const }
  const hardwareSelection = { workbenchId: 'wb-direct', worktreeId: 'wt-descendant', deviceId: null, targetKind: 'hardware' as const }
  const worktreeRow = workbenches[0]
  const descendantWorktree = workbenches[0].worktrees[0]

  const openRowMenu = async (trigger: HTMLButtonElement) => {
    await act(async () => {
      trigger.dispatchEvent(new PointerEvent('pointerdown', { bubbles: true, button: 0, ctrlKey: false }))
      trigger.dispatchEvent(new MouseEvent('click', { bubbles: true }))
    })
    return Array.from(document.body.querySelectorAll<HTMLElement>('[role="menuitem"]'))
      .map(item => item.textContent?.trim())
  }

  it('opens the hardware pages from the DEVICE row that also carries the worktree devices (AC-004, AC-009)', async () => {
    const onRefresh = vi.fn()
    const onSelectDevice = vi.fn()
    const onSelectHardware = vi.fn()
    const { host, root } = await renderNavigator(null, false, {
      selection: { workbenchId: 'wb-direct', worktreeId: 'wt-descendant', deviceId: null, targetKind: null },
      devicesByWorktree,
      onRefresh,
      onSelectDevice,
      onSelectHardware,
    })

    const deviceSection = section(host, 'device')
    expect(deviceSection).toBeTruthy()
    // The hardware target is the worktree-level one, so it is listed before its PLC devices.
    expect(Array.from(deviceSection.querySelectorAll('[data-device-target]'))
      .map(node => node.getAttribute('data-device-target'))).toEqual(['hardware', 'plc-1'])

    const refresh = deviceSection.querySelector('button[aria-label="Refresh devices"]') as HTMLButtonElement
    expect(refresh).toBeTruthy()
    // The DEVICE header carries refresh only; creation belongs to TASKS.
    expect(deviceSection.querySelector('button[aria-label^="Create"]')).toBeNull()
    await act(async () => refresh.click())
    expect(onRefresh).toHaveBeenCalledOnce()

    await act(async () => (deviceSection.querySelector('[data-device-target="plc-1"]') as HTMLElement).click())
    expect(onSelectDevice).toHaveBeenCalledWith(worktreeRow, descendantWorktree, 'plc-1')

    // This click is what makes the shell's hardware pages reachable again.
    await act(async () => (deviceSection.querySelector('[data-device-target="hardware"]') as HTMLElement).click())
    expect(onSelectHardware).toHaveBeenCalledWith(worktreeRow, descendantWorktree)

    await act(async () => root.unmount())
  })

  it('lists only the selected target tasks and creates a task bound to it (AC-005)', async () => {
    const onAddTask = vi.fn()
    const { host, root } = await renderNavigator(null, false, {
      selection: deviceSelection,
      devicesByWorktree,
      tasksByWorktree: targetTasks,
      onAddTask,
    })

    expect(section(host, 'tasks').textContent).toContain('Device task')
    expect(section(host, 'tasks').textContent).not.toContain('Hardware task')
    expect(section(host, 'tasks').textContent).not.toContain('Unbound task')

    const create = section(host, 'tasks').querySelector('button[aria-label="Create task for Main PLC"]') as HTMLButtonElement
    expect(create).toBeTruthy()
    await act(async () => create.click())
    expect(onAddTask).toHaveBeenCalledWith(worktreeRow, descendantWorktree, { kind: 'device', deviceId: 'plc-1' })

    await act(async () => root.unmount())
  })

  it('lists the worktree hardware tasks under the hardware row and nothing else (AC-009, AC-010)', async () => {
    const onAddTask = vi.fn()
    const { host, root } = await renderNavigator(null, false, {
      selection: hardwareSelection,
      devicesByWorktree,
      tasksByWorktree: targetTasks,
      onAddTask,
    })

    expect(section(host, 'tasks').textContent).toContain('Hardware task')
    expect(section(host, 'tasks').textContent).not.toContain('Device task')
    expect(section(host, 'tasks').textContent).not.toContain('Unbound task')
    // A hardware task is never the untargeted kind, so the group for those stays empty.
    expect(section(host, 'worktree').querySelector('[data-unbound-tasks]')?.textContent ?? '').not.toContain('Hardware task')

    const create = section(host, 'tasks')
      .querySelector('button[aria-label="Create task for hardware configuration"]') as HTMLButtonElement
    expect(create).toBeTruthy()
    await act(async () => create.click())
    expect(onAddTask).toHaveBeenCalledWith(worktreeRow, descendantWorktree, { kind: 'hardware' })

    await act(async () => root.unmount())
  })

  it('shows the unbound group only while the worktree holds a task that resolves to no target (AC-003)', async () => {
    const { host, root } = await renderNavigator(null, false, {
      selection: deviceSelection,
      devicesByWorktree,
      tasksByWorktree: targetTasks,
    })

    const unbound = section(host, 'worktree').querySelector('[data-unbound-tasks]') as HTMLElement
    expect(unbound).toBeTruthy()
    expect(unbound.textContent).toContain('Unbound task')
    expect(unbound.textContent).not.toContain('Device task')
    expect(unbound.textContent).not.toContain('Hardware task')
    // The group carries a task row and its menu, and nothing else: it has no creation action,
    // because a targetless task stays rejected.
    expect(Array.from(unbound.querySelectorAll('button')).map(button => button.getAttribute('aria-label')))
      .toEqual(['Open task Unbound task', 'Task actions Unbound task'])

    const withoutUnbound = { 'wb-direct:wt-descendant': targetTasks['wb-direct:wt-descendant'].filter(task => task.deviceId) }
    await act(async () => root.render(
      <WorkbenchNavigator {...navigatorProps({
        selection: deviceSelection,
        devicesByWorktree,
        tasksByWorktree: withoutUnbound,
      })} />,
    ))
    expect(section(host, 'worktree').querySelector('[data-unbound-tasks]')).toBeNull()

    await act(async () => root.unmount())
  })

  it('sizes a section to its content and releases its height when collapsed (AC-013)', async () => {
    const { host, root } = await renderNavigator(null, false, {
      selection: deviceSelection,
      devicesByWorktree,
      tasksByWorktree: targetTasks,
    })
    await expandAll(host)

    // Content-sized: a section carries its floor but no height of its own until the user drags one,
    // so it is exactly as tall as its rows rather than an equal share of the dock.
    for (const id of ['projects', 'worktree', 'device', 'tasks']) {
      const node = section(host, id)
      expect(node.style.height).toBe('')
      expect(node.style.minHeight).toBe('72px')
    }

    // Collapsed, a section holds its header only, so the sections below it move up.
    await act(async () => sectionHeader(host, 'projects').click())

    expect(section(host, 'projects').style.minHeight).toBe('')
    expect(section(host, 'projects').style.height).toBe('')
    expect(sectionBody(host, 'projects').hasAttribute('hidden')).toBe(true)
    expect(sectionHeader(host, 'worktree').getAttribute('aria-expanded')).toBe('true')
    expect(sectionIds(host)).toEqual(['projects', 'worktree', 'device', 'tasks', 'sessions'])

    await act(async () => root.unmount())
  })

  it('scrolls each section body instead of the column when the sections do not fit (AC-014)', async () => {
    const { host, root } = await renderNavigator(null, false, {
      selection: deviceSelection,
      devicesByWorktree,
      tasksByWorktree: targetTasks,
    })

    // The column never scrolls its own headers away; every header stays visible because each
    // section's body is the scroll container and can be squeezed to its floor.
    expect((host.querySelector('[data-navigator-sections]') as HTMLElement).className).toContain('overflow-hidden')
    for (const id of ['projects', 'worktree', 'device', 'tasks']) {
      expect(sectionBody(host, id).className).toContain('overflow-y-auto')
      expect(sectionBody(host, id).className).toContain('min-h-0')
    }

    await act(async () => root.unmount())
  })

  it('offers the removed device subtree operations from the device row menu (AC-008)', async () => {
    const { host, root } = await renderNavigator(null, false, {
      selection: deviceSelection,
      devicesByWorktree,
      tasksByWorktree: targetTasks,
    })

    const trigger = section(host, 'device')
      .querySelector('button[aria-label="Device actions Main PLC"]') as HTMLButtonElement

    expect(await openRowMenu(trigger)).toEqual([
      'Select device',
      'Open TIA with UI',
      'Open TIA headless',
      'Open TIA with upgrade',
      'Inspect TIA access',
      'Compare with TIA',
      'Rebuild project',
      'Update knowledge',
      'Rebuild knowledge',
    ])

    await act(async () => root.unmount())
  })

  it('offers the hardware operations from the hardware row menu (AC-009)', async () => {
    const { host, root } = await renderNavigator(null, false, {
      selection: deviceSelection,
      devicesByWorktree,
      tasksByWorktree: targetTasks,
    })

    const trigger = section(host, 'device')
      .querySelector('button[aria-label="Hardware configuration actions"]') as HTMLButtonElement

    expect(await openRowMenu(trigger)).toEqual([
      'Select hardware configuration',
      'Reload hardware configuration',
      'Compare hardware with TIA',
    ])

    await act(async () => root.unmount())
  })
})

describe('WorkbenchNavigator section sizing', () => {
  const deviceSelection = { workbenchId: 'wb-direct', worktreeId: 'wt-descendant', deviceId: 'plc-1', targetKind: 'device' as const }
  const overrides = { devicesByWorktree, tasksByWorktree: targetTasks }

  const separators = (host: HTMLElement) =>
    Array.from(host.querySelectorAll('[role="separator"]')).map(node => node.getAttribute('aria-label'))

  // happy-dom has no layout engine, so a resize supplies the geometry the same way the task list's
  // column resize test does: by mocking the measured boxes. Once a height has been applied, the mock
  // reports it, which is what a real layout would do.
  const mockHeights = (host: HTMLElement, heights: Record<string, number>) => {
    for (const [id, height] of Object.entries(heights)) {
      vi.spyOn(section(host, id), 'getBoundingClientRect').mockImplementation(() => {
        const applied = Number.parseFloat(section(host, id).style.height)
        return new DOMRect(0, 0, 200, Number.isFinite(applied) ? applied : height)
      })
    }
  }

  const dragSeparator = async (host: HTMLElement, label: string, from: number, to: number) => {
    const handle = host.querySelector(`[role="separator"][aria-label="${label}"]`) as HTMLElement
    await act(async () => handle.dispatchEvent(new PointerEvent('pointerdown', { bubbles: true, button: 0, clientY: from, pointerId: 7 })))
    await act(async () => window.dispatchEvent(new PointerEvent('pointermove', { bubbles: true, clientY: to, pointerId: 7 })))
    await act(async () => window.dispatchEvent(new PointerEvent('pointerup', { bubbles: true, clientY: to, pointerId: 7 })))
  }

  it('offers a separator between adjacent sections only, and one per pair (AC-012)', async () => {
    const { host, root } = await renderNavigator(null, false, { selection: deviceSelection, ...overrides })
    expect(separators(host)).toEqual([
      'Resize PROJECTS section', 'Resize WORKTREE section', 'Resize DEVICE section', 'Resize TASKS section',
    ])

    // Reaching the worktree but not a target drops TASKS and SESSIONS, and their separators with them.
    await act(async () => root.render(
      <WorkbenchNavigator {...navigatorProps({
        selection: { workbenchId: 'wb-direct', worktreeId: 'wt-descendant', deviceId: null, targetKind: null },
        ...overrides,
      })} />,
    ))
    expect(separators(host)).toEqual(['Resize PROJECTS section', 'Resize WORKTREE section'])

    // Nothing selected: one section, so nothing to resize it against.
    await act(async () => root.render(
      <WorkbenchNavigator {...navigatorProps({
        selection: { workbenchId: null, worktreeId: null, deviceId: null },
        ...overrides,
      })} />,
    ))
    expect(separators(host)).toEqual([])

    await act(async () => root.unmount())
  })

  it('resizes exactly the two sections a separator sits between (AC-012)', async () => {
    const { host, root } = await renderNavigator(null, false, { selection: deviceSelection, ...overrides })
    await expandAll(host)
    mockHeights(host, { projects: 200, worktree: 400 })

    await dragSeparator(host, 'Resize PROJECTS section', 100, 140)

    expect(section(host, 'projects').style.height).toBe('240px')
    expect(section(host, 'worktree').style.height).toBe('360px')
    // The sections below the pair are not part of this separator's negotiation.
    expect(section(host, 'device').style.height).toBe('')
    expect(section(host, 'tasks').style.height).toBe('')

    await act(async () => root.unmount())
  })

  it('clamps a resize at the minimum height and announces the split (AC-012)', async () => {
    const { host, root } = await renderNavigator(null, false, { selection: deviceSelection, ...overrides })
    await expandAll(host)
    mockHeights(host, { projects: 200, worktree: 400 })

    // Dragging far past the lower section's floor must stop at it, not take the section away.
    await dragSeparator(host, 'Resize PROJECTS section', 100, 500)

    expect(section(host, 'projects').style.height).toBe('528px')
    expect(section(host, 'worktree').style.height).toBe('72px')

    const handle = host.querySelector('[role="separator"][aria-label="Resize PROJECTS section"]') as HTMLElement
    expect(handle.getAttribute('aria-orientation')).toBe('horizontal')
    expect(handle.getAttribute('aria-valuemin')).toBe('72')
    expect(handle.getAttribute('aria-valuenow')).toBe('528')
    expect(handle.getAttribute('aria-valuemax')).toBe('528')

    await act(async () => root.unmount())
  })

  it('resizes the same pair from the keyboard (AC-012)', async () => {
    const first = await renderNavigator(null, false, { selection: deviceSelection, ...overrides })
    await expandAll(first.host)
    mockHeights(first.host, { projects: 200, worktree: 400 })
    const handle = first.host.querySelector('[role="separator"][aria-label="Resize PROJECTS section"]') as HTMLElement

    await act(async () => handle.dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowDown', bubbles: true })))
    expect(section(first.host, 'projects').style.height).toBe('208px')
    expect(section(first.host, 'worktree').style.height).toBe('392px')
    await act(async () => first.root.unmount())

    // A section already near the floor cannot be shrunk past it.
    const second = await renderNavigator(null, false, { selection: deviceSelection, ...overrides })
    await expandAll(second.host)
    mockHeights(second.host, { projects: 80, worktree: 400 })
    const clamped = second.host.querySelector('[role="separator"][aria-label="Resize PROJECTS section"]') as HTMLElement

    await act(async () => clamped.dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowUp', bubbles: true })))
    expect(section(second.host, 'projects').style.height).toBe('72px')
    expect(section(second.host, 'worktree').style.height).toBe('408px')

    await act(async () => second.root.unmount())
  })

  it('gives the dock\'s remaining height to the deepest section only (AC-013)', async () => {
    const fills = (host: HTMLElement) => Array.from(host.querySelectorAll('[data-section-fills]'))
      .map(node => node.getAttribute('data-navigator-section'))

    const { host, root } = await renderNavigator(null, false, { selection: deviceSelection, ...overrides })
    // Nothing sits below SESSIONS, so it reaches the dock's bottom instead of leaving that room unused.
    expect(fills(host)).toEqual(['sessions'])

    // Reaching the worktree but no target leaves DEVICE deepest, and it takes that room instead.
    await act(async () => root.render(
      <WorkbenchNavigator {...navigatorProps({
        selection: { workbenchId: 'wb-direct', worktreeId: 'wt-descendant', deviceId: null, targetKind: null },
        ...overrides,
      })} />,
    ))
    expect(fills(host)).toEqual(['device'])

    // Only one section on screen, so it is the one that fills.
    await act(async () => root.render(
      <WorkbenchNavigator {...navigatorProps({
        selection: { workbenchId: null, worktreeId: null, deviceId: null },
        ...overrides,
      })} />,
    ))
    expect(fills(host)).toEqual(['projects'])

    await act(async () => root.unmount())
  })

  it('releases the deepest section\'s room when it is collapsed (AC-013)', async () => {
    const { host, root } = await renderNavigator(null, false, { selection: deviceSelection, ...overrides })
    expect(host.querySelector('[data-section-fills]')?.getAttribute('data-navigator-section')).toBe('sessions')

    // Folding the deepest section asks for less room, not for a header over an empty box.
    await act(async () => sectionHeader(host, 'sessions').click())
    expect(host.querySelector('[data-section-fills]')).toBeNull()
    expect(sectionBody(host, 'sessions').hasAttribute('hidden')).toBe(true)

    await act(async () => sectionHeader(host, 'sessions').click())
    expect(host.querySelector('[data-section-fills]')?.getAttribute('data-navigator-section')).toBe('sessions')

    await act(async () => root.unmount())
  })
})

describe('WorkbenchNavigator sessions section', () => {
  const deviceSelection = { workbenchId: 'wb-direct', worktreeId: 'wt-descendant', deviceId: 'plc-1', targetKind: 'device' as const }
  const hardwareSelection = { workbenchId: 'wb-direct', worktreeId: 'wt-descendant', deviceId: null, targetKind: 'hardware' as const }
  const overrides = { devicesByWorktree, tasksByWorktree: targetTasks, sessionsByWorktree }

  const openRowMenu = async (trigger: HTMLButtonElement) => {
    await act(async () => {
      trigger.dispatchEvent(new PointerEvent('pointerdown', { bubbles: true, button: 0, ctrlKey: false }))
      trigger.dispatchEvent(new MouseEvent('click', { bubbles: true }))
    })
    return Array.from(document.body.querySelectorAll<HTMLElement>('[role="menuitem"]'))
  }

  it('lists the selected task\'s conversations under it, and no other task\'s (AC-015)', async () => {
    const { host, root } = await renderNavigator(null, false, {
      selection: deviceSelection, activeTaskId: 'task-device', ...overrides,
    })

    const sessions = section(host, 'sessions')
    expect(sessions).toBeTruthy()
    // The selected task is the heading; its conversation is a row inside it.
    expect(sessions.textContent).toContain('Device task')
    expect(sessions.textContent).toContain('Interlock review')
    // Another task's conversation and one bound to no task are not this list's to show.
    expect(sessions.textContent).not.toContain('Ad-hoc question')
    expect(sessions.textContent).not.toContain('Hardware task')
    expect(sessions.querySelector('[data-session-group]')?.getAttribute('data-session-group')).toBe('task-device')
    expect(sectionIds(host)).toEqual(['projects', 'worktree', 'device', 'tasks', 'sessions'])

    await act(async () => root.unmount())
  })

  it('lists every conversation of the selected device, its task-less ones first (AC-015)', async () => {
    const { host, root } = await renderNavigator(null, false, { selection: deviceSelection, ...overrides })

    const sessions = section(host, 'sessions')
    expect(sessions).toBeTruthy()
    // Nothing is selected in TASKS, so the device's whole list is what the section shows: the
    // conversations no task owns under `No task`, then one group per task that owns one.
    const groups = Array.from(sessions.querySelectorAll('[data-session-group]'))
      .map(node => node.getAttribute('data-session-group'))
    expect(groups).toEqual(['unbound', 'task-device'])
    expect(sessions.textContent).toContain('No task')
    expect(sessions.textContent).toContain('Ad-hoc question')
    expect(sessions.textContent).toContain('Device task')
    expect(sessions.textContent).toContain('Interlock review')
    // Nothing named the untargeted task, which no device conversation can be related to here.
    expect(sessions.textContent).not.toContain('Hardware task')

    await act(async () => root.unmount())
  })

  it('keeps the SESSIONS section present while a device is selected, even with nothing to list (AC-015)', async () => {
    const empty = { 'wb-direct:wt-descendant': [] }
    // A device with no conversation at all: the section is still there, saying so, with its action.
    const { host, root } = await renderNavigator(null, false, {
      selection: deviceSelection,
      devicesByWorktree,
      tasksByWorktree: targetTasks,
      sessionsByWorktree: empty,
    })
    expect(sectionIds(host)).toEqual(['projects', 'worktree', 'device', 'tasks', 'sessions'])
    expect(section(host, 'sessions').textContent).toContain('No conversations for this device yet.')
    expect(host.querySelector('button[aria-label="Start a conversation for this device"]')).not.toBeNull()

    // The same holds for a selected task that owns none, which used to take the whole section with it.
    await act(async () => root.render(
      <WorkbenchNavigator {...navigatorProps({
        selection: deviceSelection,
        activeTaskId: 'task-device',
        devicesByWorktree,
        tasksByWorktree: targetTasks,
        sessionsByWorktree: empty,
      })} />,
    ))
    expect(sectionIds(host)).toEqual(['projects', 'worktree', 'device', 'tasks', 'sessions'])
    expect(section(host, 'sessions').textContent).toContain('No conversations for this task yet.')

    // The hardware target cannot own a conversation at all, so it has neither the section nor its action.
    await act(async () => root.render(
      <WorkbenchNavigator {...navigatorProps({ selection: hardwareSelection, ...overrides })} />,
    ))
    expect(sectionIds(host)).toEqual(['projects', 'worktree', 'device', 'tasks'])
    expect(host.querySelector('button[aria-label^="Start a conversation"]')).toBeNull()

    // A worktree whose own row is the deepest selection has no device to scope a list to either.
    await act(async () => root.render(
      <WorkbenchNavigator {...navigatorProps({
        selection: { workbenchId: 'wb-direct', worktreeId: 'wt-descendant', deviceId: null },
        ...overrides,
      })} />,
    ))
    expect(sectionIds(host)).toEqual(['projects', 'worktree', 'device'])

    await act(async () => root.unmount())
  })

  it('drops the selected task when the target changes, so the device\'s task-less list is what it shows again (AC-015)', async () => {
    // A second device with its own task and one conversation no task owns. It is local to this case so
    // the shared fixtures keep describing the one-device worktree the other cases read.
    const key = 'wb-direct:wt-descendant'
    const twoDevices = { [key]: [...devicesByWorktree[key], { deviceId: 'plc-2', plcName: 'Second PLC' }] }
    const twoTasks = { [key]: [...targetTasks[key], graphTask({ taskId: 'task-second', title: 'Second task', deviceId: 'plc-2', targetKind: 'device' })] }
    const twoSessions = { [key]: [...sessionsByWorktree[key], conversation({ sessionId: 'session-second', title: 'Second device question', taskId: null, deviceId: 'plc-2' })] }
    const props = { devicesByWorktree: twoDevices, tasksByWorktree: twoTasks, sessionsByWorktree: twoSessions }

    const { host, root } = await renderNavigator(null, false, { selection: deviceSelection, ...props })
    // Picking the task makes its own conversation the section's list.
    await act(async () => (host.querySelector('button[aria-label="Open task Device task"]') as HTMLButtonElement).click())
    expect(host.querySelector('[data-task-selected="true"]')?.textContent).toContain('Device task')
    expect(section(host, 'sessions').textContent).toContain('Interlock review')

    await act(async () => root.render(
      <WorkbenchNavigator {...navigatorProps({ selection: { ...deviceSelection, deviceId: 'plc-2' }, ...props })} />,
    ))
    const other = section(host, 'sessions')
    expect(other.textContent).toContain('No task')
    expect(other.textContent).toContain('Second device question')
    expect(other.textContent).not.toContain('Interlock review')

    // Coming back to the first device is a target selection like any other, so it names no task: the
    // task the user picked before must not come back with it, or that task's list would be what the
    // device's task-less conversations are hidden behind.
    await act(async () => root.render(
      <WorkbenchNavigator {...navigatorProps({ selection: deviceSelection, ...props })} />,
    ))
    expect(host.querySelector('[data-task-selected="true"]')).toBeNull()
    const back = section(host, 'sessions')
    expect(back.textContent).toContain('No task')
    expect(back.textContent).toContain('Ad-hoc question')
    expect(back.querySelector('[data-session-group]')?.getAttribute('data-session-group')).toBe('unbound')
    // The device's whole list is back, so the conversation the dropped task owned is listed too — under
    // that task's own group, because the device names no task and the row above it is not current.
    expect(back.textContent).toContain('Device task')
    expect(back.textContent).toContain('Interlock review')

    await act(async () => root.unmount())
  })

  it('clears the selected task when its own device row is selected again (AC-015)', async () => {
    const onSelectDevice = vi.fn()
    const { host, root } = await renderNavigator(null, false, {
      selection: deviceSelection, ...overrides, onSelectDevice,
    })

    // Picking the task makes its own conversation the section's list.
    await act(async () => (host.querySelector('button[aria-label="Open task Device task"]') as HTMLButtonElement).click())
    expect(host.querySelector('[data-task-selected="true"]')?.textContent).toContain('Device task')
    expect(section(host, 'sessions').textContent).toContain('Interlock review')
    expect(section(host, 'sessions').textContent).not.toContain('Ad-hoc question')

    // Choosing the device again is the way back: it names no task, and re-activating the device that is
    // already selected changes no value the reset effect could see, so the row clears it itself.
    await act(async () => (host.querySelector('[data-device-target="plc-1"]') as HTMLElement)
      .dispatchEvent(new MouseEvent('click', { bubbles: true })))

    expect(onSelectDevice).toHaveBeenCalledWith(
      expect.objectContaining({ workbenchId: 'wb-direct' }),
      expect.objectContaining({ worktreeId: 'wt-descendant' }),
      'plc-1',
    )
    expect(host.querySelector('[data-task-selected="true"]')).toBeNull()
    const sessions = section(host, 'sessions')
    expect(sessions.textContent).toContain('No task')
    expect(sessions.textContent).toContain('Ad-hoc question')
    // The device's whole list is what a device selection shows, so the bound conversation is listed
    // under its task again rather than being hidden behind the task the row no longer names.
    expect(sessions.textContent).toContain('Interlock review')

    await act(async () => root.unmount())
  })

  it('opens a conversation from its row (AC-015)', async () => {
    const onOpenSession = vi.fn()
    const { host, root } = await renderNavigator(null, false, {
      selection: deviceSelection, activeTaskId: 'task-device', ...overrides, onOpenSession,
    })

    const row = section(host, 'sessions').querySelector('button[aria-label="Open conversation Interlock review"]') as HTMLButtonElement
    expect(row).toBeTruthy()
    await act(async () => row.click())

    // The row hands over the conversation, which carries the device that owns it.
    expect(onOpenSession).toHaveBeenCalledWith(
      expect.objectContaining({ sessionId: 'session-bound', deviceId: 'plc-1', taskId: 'task-device' }))
    await act(async () => root.unmount())
  })

  it('starts a conversation in the scope the section is showing from its header (AC-016)', async () => {
    const onAddSession = vi.fn()
    // With no task selected the section is the device's task-less list, so its header starts a
    // device-scoped conversation that no task owns — exactly the list it is showing.
    const { host, root } = await renderNavigator(null, false, {
      selection: deviceSelection, ...overrides, onAddSession,
    })
    expect(section(host, 'sessions')).toBeTruthy()
    const startDevice = section(host, 'sessions')
      .querySelector('button[aria-label="Start a conversation for this device"]') as HTMLButtonElement
    expect(startDevice).toBeTruthy()
    await act(async () => startDevice.click())
    expect(onAddSession).toHaveBeenCalledWith(null)

    // Selecting the task that owns the conversations binds the new one to that task, named for it.
    await act(async () => root.render(
      <WorkbenchNavigator {...navigatorProps({
        selection: deviceSelection, activeTaskId: 'task-device', ...overrides, onAddSession,
      })} />,
    ))
    const start = section(host, 'sessions')
      .querySelector('button[aria-label="Start a conversation for Device task"]') as HTMLButtonElement
    expect(start).toBeTruthy()
    await act(async () => start.click())

    expect(onAddSession).toHaveBeenCalledWith(expect.objectContaining({ taskId: 'task-device' }))
    await act(async () => root.unmount())
  })

  it('keeps the selected task after the task detail yields the main area (AC-015)', async () => {
    // Opening a conversation closes the detail, so `activeTaskId` goes away while the user is still
    // working in that task: the row and the section have to stay where they were.
    const { host, root } = await renderNavigator(null, false, {
      selection: deviceSelection, activeTaskId: 'task-device', ...overrides,
    })
    expect(host.querySelector('[data-task-selected="true"]')?.textContent).toContain('Device task')

    await act(async () => root.render(
      <WorkbenchNavigator {...navigatorProps({ selection: deviceSelection, activeTaskId: null, ...overrides })} />,
    ))

    expect(host.querySelector('[data-task-selected="true"]')?.textContent).toContain('Device task')
    expect(section(host, 'sessions').textContent).toContain('Interlock review')
    expect(section(host, 'sessions').textContent).not.toContain('Ad-hoc question')

    await act(async () => root.unmount())
  })

  it('offers every operation the repository performs on a conversation row (AC-017)', async () => {
    const { host, root } = await renderNavigator(null, false, {
      selection: deviceSelection, ...overrides,
    })
    // A conversation that no task is related to can be opened, renamed, exported, related to the
    // worktree's tasks or deleted. The row menu is its only entry point, so it has to carry all of
    // them — binding and clearing are the one picker entry, because a second click clears a check.
    const unbound = section(host, 'sessions')
      .querySelector('button[aria-label="Conversation actions Ad-hoc question"]') as HTMLButtonElement
    // The row menu is visible without hovering, like every other row's menu in the navigator.
    const conversationTriggerClasses = unbound.className.split(/\s+/)
    expect(conversationTriggerClasses).not.toContain('opacity-0')
    expect(conversationTriggerClasses).not.toContain('pointer-events-none')

    expect((await openRowMenu(unbound)).map(item => item.textContent?.trim())).toEqual([
      'Open conversation',
      'Rename conversation',
      'Export conversation',
      'Tasks…',
      'Delete conversation',
    ])

    // A related conversation offers the same single binding entry, never a second one to remove it.
    await act(async () => root.render(
      <WorkbenchNavigator {...navigatorProps({
        selection: deviceSelection, activeTaskId: 'task-device', ...overrides,
      })} />,
    ))
    const bound = section(host, 'sessions')
      .querySelector('button[aria-label="Conversation actions Interlock review"]') as HTMLButtonElement
    expect((await openRowMenu(bound)).map(item => item.textContent?.trim())).toEqual([
      'Open conversation',
      'Rename conversation',
      'Export conversation',
      'Tasks…',
      'Delete conversation',
    ])
    await act(async () => root.unmount())
  })

  it('exports a conversation from its row menu', async () => {
    const onExportSession = vi.fn()
    const { host, root } = await renderNavigator(null, false, {
      selection: deviceSelection, ...overrides, onExportSession,
    })
    const trigger = section(host, 'sessions')
      .querySelector('button[aria-label="Conversation actions Ad-hoc question"]') as HTMLButtonElement
    const item = (await openRowMenu(trigger)).find(entry => entry.textContent?.trim() === 'Export conversation')!

    await act(async () => item.dispatchEvent(new MouseEvent('click', { bubbles: true })))

    expect(onExportSession).toHaveBeenCalledWith(
      expect.objectContaining({ sessionId: 'session-unbound', deviceId: 'plc-1' }))
    await act(async () => root.unmount())
  })

  it('applies a conversation\'s whole relation set from a checked picker (AC-019)', async () => {
    const onSetSessionTasks = vi.fn()
    // Two device tasks that can own a conversation, and a conversation already related to both.
    const key = 'wb-direct:wt-descendant'
    const twoTasks = {
      [key]: [...targetTasks[key], graphTask({ taskId: 'task-alarm', title: 'Alarm task', deviceId: 'plc-1', targetKind: 'device' })],
    }
    const twoRelations = {
      [key]: [
        conversation({
          sessionId: 'session-bound', title: 'Interlock review', taskId: 'task-device', deviceId: 'plc-1',
          taskProvenance: 'manual',
          taskRelations: [relation('task-device', { isPrimary: true }), relation('task-alarm', { provenance: 'auto' })],
        }),
      ],
    }
    const { host, root } = await renderNavigator(null, false, {
      selection: deviceSelection, activeTaskId: 'task-device',
      devicesByWorktree, tasksByWorktree: twoTasks, sessionsByWorktree: twoRelations, onSetSessionTasks,
    })

    const trigger = section(host, 'sessions')
      .querySelector('button[aria-label="Conversation actions Interlock review"]') as HTMLButtonElement
    const entry = (await openRowMenu(trigger)).find(item => item.textContent?.trim() === 'Tasks…')!
    await act(async () => entry.dispatchEvent(new MouseEvent('click', { bubbles: true })))

    // One check per task that can own a conversation, reflecting the relations it already has. A task
    // with no device is not offered at all, because it could not own a conversation.
    const options = () => Array.from(document.body.querySelectorAll<HTMLElement>('[role="option"]'))
    expect(options().map(option => [option.textContent?.trim(), option.getAttribute('aria-checked')])).toEqual([
      ['Device task', 'true'],
      ['Alarm task', 'true'],
    ])
    expect(options().map(option => option.textContent?.trim())).not.toContain('Unbound task')

    // A second click on a checked task clears that check and writes nothing yet: the set is one
    // operation, so the conversation is never left half-linked while the user is still choosing.
    const alarm = options().find(option => option.textContent?.trim() === 'Alarm task')!
    await act(async () => alarm.dispatchEvent(new MouseEvent('click', { bubbles: true })))
    expect(options().find(option => option.textContent?.trim() === 'Alarm task')?.getAttribute('aria-checked')).toBe('false')
    expect(options().find(option => option.textContent?.trim() === 'Device task')?.getAttribute('aria-checked')).toBe('true')
    expect(onSetSessionTasks).not.toHaveBeenCalled()

    // One apply carries the whole set, naming the primary it read while that task is still checked.
    const apply = Array.from(document.body.querySelectorAll('button')).find(button => button.textContent?.trim() === 'Apply')!
    await act(async () => apply.dispatchEvent(new MouseEvent('click', { bubbles: true })))
    expect(onSetSessionTasks).toHaveBeenCalledTimes(1)
    expect(onSetSessionTasks).toHaveBeenCalledWith(
      expect.objectContaining({ sessionId: 'session-bound' }), ['task-device'], 'task-device')

    await act(async () => root.unmount())
  })

  it('relates a task-less conversation to one of the worktree\'s tasks, and clears a relation (AC-019)', async () => {
    const onSetSessionTasks = vi.fn()
    // The task-less row offers the same binding entry, and the picker lists the worktree's tasks.
    const { host, root } = await renderNavigator(null, false, {
      selection: deviceSelection, ...overrides, onSetSessionTasks,
    })
    const trigger = section(host, 'sessions')
      .querySelector('button[aria-label="Conversation actions Ad-hoc question"]') as HTMLButtonElement
    const bind = (await openRowMenu(trigger)).find(entry => entry.textContent?.trim() === 'Tasks…')!
    await act(async () => bind.dispatchEvent(new MouseEvent('click', { bubbles: true })))

    // A task that cannot own a conversation is not a valid relation, so it is not offered.
    const offered = Array.from(document.body.querySelectorAll<HTMLElement>('[role="option"]'))
      .map(option => option.textContent?.trim())
    expect(offered).toEqual(['Device task'])
    expect(offered).not.toContain('Hardware task')
    expect(offered).not.toContain('Unbound task')

    const search = document.body.querySelector<HTMLInputElement>('input[aria-label="Search this worktree\'s tasks"]')!
    expect(search).toBeTruthy()
    // The keyboard checks a task exactly as a click does, and the apply is still one operation.
    await act(async () => {
      search.dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowDown', bubbles: true }))
      search.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true }))
    })
    expect(onSetSessionTasks).not.toHaveBeenCalled()
    const apply = Array.from(document.body.querySelectorAll('button')).find(button => button.textContent?.trim() === 'Apply')!
    await act(async () => apply.dispatchEvent(new MouseEvent('click', { bubbles: true })))
    // The conversation had no primary to keep, so the primary is left to the set's own resolution.
    expect(onSetSessionTasks).toHaveBeenCalledTimes(1)
    const [session, taskIds, primary] = onSetSessionTasks.mock.calls[0]!
    expect(session).toMatchObject({ sessionId: 'session-unbound' })
    expect(taskIds).toEqual(['task-device'])
    expect(primary).toBeUndefined()

    // Clearing every check is one apply too: the set it writes is empty.
    await act(async () => root.render(
      <WorkbenchNavigator {...navigatorProps({
        selection: deviceSelection, activeTaskId: 'task-device', ...overrides, onSetSessionTasks,
      })} />,
    ))
    const boundTrigger = section(host, 'sessions')
      .querySelector('button[aria-label="Conversation actions Interlock review"]') as HTMLButtonElement
    const rebind = (await openRowMenu(boundTrigger)).find(entry => entry.textContent?.trim() === 'Tasks…')!
    await act(async () => rebind.dispatchEvent(new MouseEvent('click', { bubbles: true })))
    const checked = Array.from(document.body.querySelectorAll<HTMLElement>('[role="option"]'))
      .find(option => option.getAttribute('aria-checked') === 'true')!
    await act(async () => checked.dispatchEvent(new MouseEvent('click', { bubbles: true })))
    const clearApply = Array.from(document.body.querySelectorAll('button')).find(button => button.textContent?.trim() === 'Apply')!
    await act(async () => clearApply.dispatchEvent(new MouseEvent('click', { bubbles: true })))

    // The whole set is written as itself, so clearing every check clears every relation.
    expect(onSetSessionTasks).toHaveBeenCalledTimes(2)
    const [clearedSession, clearedIds, clearedPrimary] = onSetSessionTasks.mock.calls[1]!
    expect(clearedSession).toMatchObject({ sessionId: 'session-bound' })
    expect(clearedIds).toEqual([])
    expect(clearedPrimary).toBeUndefined()
    await act(async () => root.unmount())
  })

  it('lists a conversation related to several tasks under each of them, and never as task-less (AC-015)', async () => {
    const key = 'wb-direct:wt-descendant'
    // The same conversation is related to two device tasks; a second one is related to nothing.
    const twoTasks = {
      [key]: [...targetTasks[key], graphTask({ taskId: 'task-alarm', title: 'Alarm task', deviceId: 'plc-1', targetKind: 'device' })],
    }
    const shared = {
      [key]: [
        conversation({
          sessionId: 'session-shared', title: 'Shared finding', taskId: 'task-device', deviceId: 'plc-1',
          taskProvenance: 'manual',
          taskRelations: [relation('task-device', { isPrimary: true }), relation('task-alarm')],
        }),
        conversation({ sessionId: 'session-unbound', title: 'Ad-hoc question', taskId: null, deviceId: 'plc-1' }),
      ],
    }
    const props = { devicesByWorktree, tasksByWorktree: twoTasks, sessionsByWorktree: shared }

    // The first task's list holds it, under that task's heading.
    const { host, root } = await renderNavigator(null, false, {
      selection: deviceSelection, activeTaskId: 'task-device', ...props,
    })
    expect(section(host, 'sessions').textContent).toContain('Shared finding')
    expect(section(host, 'sessions').querySelector('[data-session-group]')?.getAttribute('data-session-group')).toBe('task-device')
    expect(section(host, 'sessions').textContent).not.toContain('Ad-hoc question')

    // The second task lists the same conversation, because membership is a set and not one field.
    await act(async () => root.render(
      <WorkbenchNavigator {...navigatorProps({ selection: deviceSelection, activeTaskId: 'task-alarm', ...props })} />,
    ))
    expect(section(host, 'sessions').textContent).toContain('Alarm task')
    expect(section(host, 'sessions').textContent).toContain('Shared finding')
    expect(section(host, 'sessions').querySelector('[data-session-group]')?.getAttribute('data-session-group')).toBe('task-alarm')

    // With no task selected the device's whole list is shown, and the shared conversation sits under
    // each task that owns it — never under `No task`, which is the conversation with no relation.
    await act(async () => root.unmount())
    const taskless = await renderNavigator(null, false, { selection: deviceSelection, ...props })
    expect(sessionGroup(taskless.host, 'unbound').textContent).toContain('Ad-hoc question')
    expect(sessionGroup(taskless.host, 'unbound').textContent).not.toContain('Shared finding')
    expect(sessionGroup(taskless.host, 'task-device').textContent).toContain('Shared finding')
    expect(sessionGroup(taskless.host, 'task-alarm').textContent).toContain('Shared finding')

    await act(async () => taskless.root.unmount())
  })

  it('moves a conversation between the task list and the task-less list when its binding changes', async () => {
    // Bound: the conversation is in the list of the task it names, under that task's heading.
    const rebound = {
      'wb-direct:wt-descendant': [
        sessionsByWorktree['wb-direct:wt-descendant'][0],
        { ...sessionsByWorktree['wb-direct:wt-descendant'][1], taskId: 'task-device' },
      ],
    }
    const bound = await renderNavigator(null, false, {
      selection: deviceSelection, activeTaskId: 'task-device',
      devicesByWorktree, tasksByWorktree: targetTasks, sessionsByWorktree: rebound,
    })
    expect(section(bound.host, 'sessions').querySelector('[data-session-group]')?.getAttribute('data-session-group')).toBe('task-device')
    expect(section(bound.host, 'sessions').textContent).toContain('Ad-hoc question')
    await act(async () => bound.root.unmount())

    // Cleared: with the device selected and no task named, the cleared conversation is back in the
    // device's task-less group while the bound one sits under its own task's group.
    const cleared = await renderNavigator(null, false, { selection: deviceSelection, ...overrides })
    expect(sessionGroup(cleared.host, 'unbound').textContent).toContain('Ad-hoc question')
    expect(sessionGroup(cleared.host, 'unbound').textContent).not.toContain('Interlock review')
    expect(sessionGroup(cleared.host, 'task-device').textContent).toContain('Interlock review')
    await act(async () => cleared.root.unmount())
  })

  it('offers the task row menu without hovering too', async () => {
    const { host, root } = await renderNavigator(null, false, {
      selection: deviceSelection, activeTaskId: 'task-device', ...overrides,
    })
    const trigger = section(host, 'tasks')
      .querySelector('button[aria-label="Task actions Device task"]') as HTMLButtonElement
    const taskTriggerClasses = trigger.className.split(/\s+/)
    expect(taskTriggerClasses).not.toContain('opacity-0')
    expect(taskTriggerClasses).not.toContain('pointer-events-none')

    const items = (await openRowMenu(trigger)).map(item => item.textContent?.trim())
    expect(items).toEqual(['Change status', 'Change type and icon', 'Rename task'])
    await act(async () => root.unmount())
  })

  it('shows the worktree row\'s expand toggle only when that row has an unbound group', async () => {
    const withUnbound = await renderNavigator(null, false, {
      selection: { workbenchId: 'wb-direct', worktreeId: 'wt-descendant', deviceId: 'plc-1', targetKind: 'device' },
      devicesByWorktree,
      tasksByWorktree: targetTasks,
    })
    const toggle = () => withUnbound.host
      .querySelector('[data-worktree-row="wt-descendant"] svg.lucide-plus, [data-worktree-row="wt-descendant"] svg.lucide-minus')
    expect(toggle()).not.toBeNull()
    await act(async () => withUnbound.root.unmount())

    // Without a task that resolves to no target there is nothing to expand, so the row shows no toggle.
    const boundOnly = { 'wb-direct:wt-descendant': targetTasks['wb-direct:wt-descendant'].filter(task => task.deviceId) }
    const withoutUnbound = await renderNavigator(null, false, {
      selection: { workbenchId: 'wb-direct', worktreeId: 'wt-descendant', deviceId: 'plc-1', targetKind: 'device' },
      devicesByWorktree,
      tasksByWorktree: boundOnly,
    })
    expect(withoutUnbound.host
      .querySelector('[data-worktree-row="wt-descendant"] svg.lucide-plus, [data-worktree-row="wt-descendant"] svg.lucide-minus')).toBeNull()
    await act(async () => withoutUnbound.root.unmount())
  })

  it('renames a conversation from its row menu', async () => {
    const onRenameSession = vi.fn()
    const { host, root } = await renderNavigator(null, false, {
      selection: deviceSelection, activeTaskId: 'task-device', ...overrides, onRenameSession,
    })
    const trigger = section(host, 'sessions')
      .querySelector('button[aria-label="Conversation actions Interlock review"]') as HTMLButtonElement
    const rename = (await openRowMenu(trigger)).find(item => item.textContent?.trim() === 'Rename conversation')!

    await act(async () => rename.dispatchEvent(new MouseEvent('click', { bubbles: true })))
    const input = document.body.querySelector('input[aria-label="Conversation title"]') as HTMLInputElement
    expect(input?.value).toBe('Interlock review')
    await act(async () => {
      Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value')?.set?.call(input, 'Renamed conversation')
      input.dispatchEvent(new Event('input', { bubbles: true }))
    })
    const save = Array.from(document.body.querySelectorAll('button')).find(button => button.textContent?.includes('Save'))!
    await act(async () => save.dispatchEvent(new MouseEvent('click', { bubbles: true })))

    expect(onRenameSession).toHaveBeenCalledWith(
      expect.objectContaining({ sessionId: 'session-bound', deviceId: 'plc-1' }), 'Renamed conversation')
    await act(async () => root.unmount())
  })

  it('asks before deleting a conversation, and only deletes when confirmed (AC-017)', async () => {
    const onDeleteSession = vi.fn()
    // happy-dom has no window.confirm; the browser does, and the row uses it to ask.
    const confirm = vi.fn(() => false)
    vi.stubGlobal('confirm', confirm)
    const { host, root } = await renderNavigator(null, false, {
      selection: deviceSelection, ...overrides, onDeleteSession,
    })

    const deleteRow = async (title: string) => {
      const trigger = section(host, 'sessions')
        .querySelector(`button[aria-label="Conversation actions ${title}"]`) as HTMLButtonElement
      const item = (await openRowMenu(trigger)).find(entry => entry.textContent?.trim() === 'Delete conversation')!
      await act(async () => item.dispatchEvent(new MouseEvent('click', { bubbles: true })))
    }

    // A conversation that no task is related to has no such link to lose, so its confirmation does not
    // claim one (AC-017).
    await deleteRow('Ad-hoc question')
    expect(confirm).toHaveBeenCalled()
    expect(confirm.mock.calls[0]?.[0]).not.toContain('links to the tasks it is related to are lost')
    expect(onDeleteSession).not.toHaveBeenCalled()

    // Selecting the task that owns the other conversation switches the list to it, and that
    // confirmation says what the delete costs, because the link to the task goes with it.
    confirm.mockClear()
    await act(async () => root.render(
      <WorkbenchNavigator {...navigatorProps({
        selection: deviceSelection, activeTaskId: 'task-device', ...overrides, onDeleteSession,
      })} />,
    ))
    await deleteRow('Interlock review')
    expect(confirm.mock.calls[0]?.[0]).toContain('links to the tasks it is related to are lost')
    expect(onDeleteSession).not.toHaveBeenCalled()

    confirm.mockReturnValue(true)
    await deleteRow('Interlock review')
    expect(onDeleteSession).toHaveBeenCalledWith(
      expect.objectContaining({ sessionId: 'session-bound', deviceId: 'plc-1' }))

    vi.unstubAllGlobals()
    await act(async () => root.unmount())
  })
})

describe('WorkbenchNavigator section cascade', () => {
  /** The one-device worktree every accordion case below walks down. */
  const deviceSelection = {
    workbenchId: 'wb-direct', worktreeId: 'wt-descendant', deviceId: 'plc-1', targetKind: 'device' as const,
  }
  const overrides = { devicesByWorktree, tasksByWorktree: targetTasks }

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

  it('folds the level a scope was picked from and opens the one below it (AC-007)', async () => {
    const { host, root } = await renderNavigator(null, false, {
      selection: { workbenchId: null, worktreeId: null, deviceId: null }, ...overrides,
    })
    // Nothing is selected, so there is no level to fold and PROJECTS is what the user picks from.
    expect(expandedIds(host)).toEqual(['projects'])

    // Selecting a workbench folds PROJECTS and opens WORKTREE.
    await act(async () => root.render(
      <WorkbenchNavigator {...navigatorProps({
        selection: { workbenchId: 'wb-direct', worktreeId: null, deviceId: null }, ...overrides,
      })} />,
    ))
    expect(expandedIds(host)).toEqual(['worktree'])
    expect(sectionBody(host, 'projects').hasAttribute('hidden')).toBe(true)

    // Selecting a worktree folds WORKTREE and opens DEVICE.
    await act(async () => root.render(
      <WorkbenchNavigator {...navigatorProps({
        selection: { workbenchId: 'wb-direct', worktreeId: 'wt-descendant', deviceId: null }, ...overrides,
      })} />,
    ))
    expect(expandedIds(host)).toEqual(['device'])
    expect(sectionBody(host, 'worktree').hasAttribute('hidden')).toBe(true)

    // Selecting a device folds DEVICE and opens TASKS — and leaves SESSIONS open with it, because the
    // device is selected both to work on its tasks and to read its conversations.
    await act(async () => root.render(
      <WorkbenchNavigator {...navigatorProps({ selection: deviceSelection, ...overrides })} />,
    ))
    expect(expandedIds(host)).toEqual(['tasks', 'sessions'])
    expect(sectionBody(host, 'device').hasAttribute('hidden')).toBe(true)

    // The hardware row is a target too, and it has no conversation list to leave open.
    await act(async () => root.render(
      <WorkbenchNavigator {...navigatorProps({
        selection: { workbenchId: 'wb-direct', worktreeId: 'wt-descendant', deviceId: null, targetKind: 'hardware' },
        ...overrides,
      })} />,
    ))
    expect(expandedIds(host)).toEqual(['tasks'])

    await act(async () => root.unmount())
  })

  it('keeps a header the user opened until the scope selection moves again (AC-007)', async () => {
    const { host, root } = await renderNavigator(null, false, { selection: deviceSelection, ...overrides })
    expect(expandedIds(host)).toEqual(['tasks', 'sessions'])

    // Reading the worktree's projects again is the user's own move, and nothing takes it back until
    // the scope selection changes.
    await act(async () => sectionHeader(host, 'projects').click())
    expect(expandedIds(host)).toEqual(['projects', 'tasks', 'sessions'])

    // Picking another device is that change: the accordion's defaults come back.
    await act(async () => root.render(
      <WorkbenchNavigator {...navigatorProps({
        selection: { ...deviceSelection, deviceId: 'plc-2' }, ...overrides,
      })} />,
    ))
    expect(expandedIds(host)).toEqual(['tasks', 'sessions'])

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

    // Selecting the workbench folds the level it was picked from and opens the one below it, so the
    // case starts from PROJECTS folded and WORKTREE open.
    expect(projectsHeader.getAttribute('aria-expanded')).toBe('false')
    expect(projectsHeader.getAttribute('aria-controls')).toBe(projectsBody.id)
    expect(projectsBody.hasAttribute('hidden')).toBe(true)
    expect(worktreeHeader.getAttribute('aria-expanded')).toBe('true')
    expect(worktreeBody.hasAttribute('hidden')).toBe(false)

    // The header must sit outside the body's scroll region, otherwise a long section could
    // scroll its own header out of view. Scrolling a non-ancestor can never move it.
    expect(projectsBody.contains(projectsHeader)).toBe(false)
    expect(worktreeBody.contains(worktreeHeader)).toBe(false)

    await act(async () => projectsHeader.click())

    // Activating one header changes that section alone: the other keeps the state it had.
    expect(projectsHeader.getAttribute('aria-expanded')).toBe('true')
    expect(projectsBody.hasAttribute('hidden')).toBe(false)
    expect(worktreeHeader.getAttribute('aria-expanded')).toBe('true')
    expect(worktreeBody.hasAttribute('hidden')).toBe(false)

    await act(async () => projectsHeader.click())

    expect(projectsHeader.getAttribute('aria-expanded')).toBe('false')
    expect(projectsBody.hasAttribute('hidden')).toBe(true)
    expect(worktreeHeader.getAttribute('aria-expanded')).toBe('true')
    expect(worktreeBody.hasAttribute('hidden')).toBe(false)
    await act(async () => root.unmount())
  })
})

/**
 * Item 007: the navigator renders one row treatment across all five row kinds. These cases assert the
 * *equality of the class sets* between kinds, never the presence of an individual class, so a kind that
 * drifts away from the shared treatment fails here even if it still carries the classes someone expects.
 */
describe('WorkbenchNavigator shared row treatment', () => {
  const selection = { workbenchId: 'wb-direct', worktreeId: 'wt-descendant', deviceId: 'plc-1', targetKind: 'device' as const }
  /** Two rows of every kind, so each kind has one current and one non-current row to compare. */
  const rowTasks: Record<string, api.EngineeringTask[]> = {
    'wb-direct:wt-descendant': [
      graphTask({ taskId: 'task-current', title: 'Current task', deviceId: 'plc-1', targetKind: 'device' }),
      graphTask({ taskId: 'task-other', title: 'Other task', deviceId: 'plc-1', targetKind: 'device' }),
    ],
  }
  const rowSessions: Record<string, api.ChatSessionInfo[]> = {
    'wb-direct:wt-descendant': [
      conversation({ sessionId: 'session-current', title: 'Open conversation', taskId: 'task-current', deviceId: 'plc-1' }),
      conversation({ sessionId: 'session-other', title: 'Other conversation', taskId: 'task-current', deviceId: 'plc-1' }),
    ],
  }
  const shared = {
    selection,
    devicesByWorktree,
    tasksByWorktree: rowTasks,
    sessionsByWorktree: rowSessions,
  }
  const renderAllKinds = (overrides: Partial<NavigatorProps> = {}) => renderNavigator(null, false, {
    ...shared,
    activeTaskId: 'task-current',
    activeSessionId: 'session-current',
    ...overrides,
  })

  /** `DEVICE` is one kind with two row types: the hardware row and the device rows. */
  const kindSelectors: Record<string, string> = {
    projects: '[data-navigator-row="projects"]',
    worktree: '[data-navigator-row="worktree"]',
    device: '[data-navigator-row="hardware"], [data-navigator-row="device"]',
    tasks: '[data-navigator-row="tasks"]',
    sessions: '[data-navigator-row="sessions"]',
  }
  const classSet = (element: Element) => new Set(element.className.split(/\s+/).filter(Boolean))
  /** The current-row marker is the one state difference; the geometry around it is what must be equal. */
  const markers = new Set(['bg-accent/50', 'hover:bg-accent/40'])
  /** The only allowed geometry difference: the menu gutter `TASKS`/`SESSIONS` rows add for their menu. */
  const menuGutter = new Set(['relative', 'pr-8'])
  const geometry = (element: Element) =>
    new Set([...classSet(element)].filter(className => !markers.has(className) && !menuGutter.has(className)))
  /** lucide stamps its own `lucide`/`lucide-<name>` classes on every icon; the treatment is the rest. */
  const iconTreatment = (element: Element) =>
    new Set([...classSet(element)].filter(className => !className.startsWith('lucide')))
  const rowsFor = (host: HTMLElement, selector: string) => Array.from(host.querySelectorAll<HTMLElement>(selector))
  const currentRow = (host: HTMLElement, selector: string) =>
    rowsFor(host, selector).find(row => row.hasAttribute('aria-current'))
  const otherRow = (host: HTMLElement, selector: string) =>
    rowsFor(host, selector).find(row => !row.hasAttribute('aria-current'))

  it('gives all five row kinds one class set, one current marker and always-gray icons', async () => {
    const { host, root } = await renderAllKinds()

    const reference = geometry(currentRow(host, kindSelectors.projects)!)
    for (const [kind, selector] of Object.entries(kindSelectors)) {
      const current = currentRow(host, selector)
      const other = otherRow(host, selector)
      expect(current, `${kind} current row`).toBeTruthy()
      expect(other, `${kind} non-current row`).toBeTruthy()

      const rows = rowsFor(host, selector)
      const needsMenuGutter = kind === 'tasks' || kind === 'sessions'
      for (const row of rows) {
        // The row geometry is one set across kinds. `TASKS` and `SESSIONS` rows additionally reserve the
        // gutter their absolutely positioned 3-dots menu sits in; that is the only allowed difference,
        // and it is pinned here so it can neither spread to another kind nor go missing from these two.
        expect(geometry(row), `${kind} row against the reference treatment`).toEqual(reference)
        expect(classSet(row).has('relative'), `${kind} row menu gutter`).toBe(needsMenuGutter)
        expect(classSet(row).has('pr-8'), `${kind} row menu gutter`).toBe(needsMenuGutter)
      }

      // One current-row marker: `bg-accent/50` on the current row, its sibling left unmarked.
      expect(current!.className).toContain('bg-accent/50')
      expect(other!.className).not.toContain('bg-accent/50')

      for (const row of rows) {
        // The row's identity icon is the `h-4` one; a row may also hold a smaller disclosure toggle, the
        // knowledge dot, or the 3-dots trigger, none of which is the row icon.
        const icon = Array.from(row.querySelectorAll('svg')).find(node => node.classList.contains('h-4'))
        expect(icon, `${kind} row icon`).toBeTruthy()
        expect(iconTreatment(icon!), `${kind} row icon treatment`).toEqual(
          new Set(['h-4', 'w-4', 'shrink-0', 'text-muted-foreground']))
        expect(row.className).not.toMatch(/text-chart-\d/)
        expect(row.className).not.toMatch(/\brounded-md\b/)
        expect(row.className).not.toMatch(/border-ring/)
        expect(row.className).not.toMatch(/(^|\s)border(\s|$)/)
      }
    }

    await act(async () => root.unmount())
  })

  it('keeps the row rhythm and the headings that align with the icon column', async () => {
    const { host, root } = await renderAllKinds()

    // `mb-1` is the row rhythm: `TASKS` and `SESSIONS` rows used to sit flush against each other.
    for (const selector of Object.values(kindSelectors)) {
      for (const row of host.querySelectorAll<HTMLElement>(selector)) {
        expect(row.className.split(/\s+/)).toContain('mb-1')
      }
    }

    // The group headings sit in the row icon column, so they move with it (px-2 -> px-1).
    const heading = host.querySelector<HTMLElement>('#navigator-section-sessions [data-session-group] > div')
    expect(heading?.textContent).toContain('Current task')
    expect(heading?.className.split(/\s+/)).toContain('px-1')

    await act(async () => root.unmount())
  })

  it('marks the open conversation without moving any other row\'s current marker (AC-018)', async () => {
    const { host, root } = await renderAllKinds()

    const sessions = rowsFor(host, kindSelectors.sessions)
    const open = sessions.find(row => row.getAttribute('data-session-open') === 'session-current')!
    const other = sessions.find(row => row.getAttribute('data-session-open') === 'session-other')!
    expect(open.getAttribute('aria-current')).toBe('true')
    expect(open.className).toContain('bg-accent/50')
    expect(other.hasAttribute('aria-current')).toBe(false)
    expect(other.className).not.toContain('bg-accent/50')

    // The marker is not a selection: the device row and the task row keep their own current markers.
    expect(currentRow(host, '[data-navigator-row="device"]')?.getAttribute('aria-current')).toBe('true')
    expect(currentRow(host, '[data-navigator-row="tasks"]')?.getAttribute('aria-current')).toBe('page')

    // With the prop omitted the navigator renders exactly as before: no conversation is marked.
    await act(async () => root.render(
      <WorkbenchNavigator {...navigatorProps({ ...shared, activeTaskId: 'task-current' })} />,
    ))
    const unmarked = rowsFor(host, kindSelectors.sessions)
    expect(unmarked).toHaveLength(2)
    for (const row of unmarked) {
      expect(row.hasAttribute('aria-current')).toBe(false)
      expect(row.className).not.toContain('bg-accent/50')
    }

    await act(async () => root.unmount())
  })
})
