// @vitest-environment happy-dom
import React, { act } from 'react'
import { createRoot } from 'react-dom/client'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import * as api from '@/api/client'
import WorktreeTasksPanel, { type TaskViewMode } from './WorktreeTasksPanel'

globalThis.IS_REACT_ACT_ENVIRONMENT = true

const task = (overrides: Partial<api.WorktreeTask>): api.WorktreeTask => ({
  taskId: 'task-1',
  title: 'Rework FB_Motor_Control',
  details: null,
  status: 'todo',
  elementRefs: [],
  createdUtc: '2026-08-01T00:00:00Z',
  doneUtc: null,
  ...overrides,
})

const tasks: api.WorktreeTask[] = [
  task({ taskId: 't-todo', title: 'Todo task' }),
  task({ taskId: 't-progress', title: 'Active task', status: 'inProgress' }),
  task({
    taskId: 't-done',
    title: 'Finished task',
    status: 'done',
    doneUtc: '2026-08-02T00:00:00Z',
    details: '**Swap** the sensor scaling',
    elementRefs: ['Device01/FB_Scale'],
  }),
]

vi.mock('@/api/client', async importOriginal => {
  const actual = await importOriginal<typeof import('@/api/client')>()
  return {
    ...actual,
    createGraphWorktreeTask: vi.fn(async (_wb: string, _wt: string, body: { title: string }) =>
      task({ taskId: 't-new', title: body.title })),
    listDevices: vi.fn(async () => [{ deviceId: 'device-1', plcName: 'Main PLC' }]),
    listDeviceSessions: vi.fn(async () => []),
    updateWorktreeTask: vi.fn(async (_wb: string, _wt: string, taskId: string, patch: Partial<api.WorktreeTask>) =>
      task({ taskId, title: patch.title ?? 'updated', status: patch.status ?? 'todo' })),
    deleteWorktreeTask: vi.fn(async () => undefined),
  }
})

const renderPanel = async (overrides: Partial<React.ComponentProps<typeof WorktreeTasksPanel>> = {}) => {
  const onChanged = vi.fn()
  const host = document.createElement('div')
  document.body.appendChild(host)
  const root = createRoot(host)
  function Harness() {
    const [viewMode, setViewMode] = React.useState<TaskViewMode>('cards')
    return <WorktreeTasksPanel
      workbenchId="wb1"
      worktreeId="wt1"
      tasks={tasks}
      loading={false}
      error={null}
      onChanged={onChanged}
      deviceIds={['device-1']}
      viewMode={viewMode}
      onViewModeChange={setViewMode}
      {...overrides}
    />
  }
  await act(async () => root.render(<Harness />))
  return { host, root, onChanged }
}

const setInputValue = (input: HTMLInputElement | HTMLTextAreaElement, value: string) => {
  const prototype = input instanceof HTMLTextAreaElement
    ? window.HTMLTextAreaElement.prototype
    : window.HTMLInputElement.prototype
  const setter = Object.getOwnPropertyDescriptor(prototype, 'value')!.set!
  setter.call(input, value)
  input.dispatchEvent(new Event('input', { bubbles: true }))
}

afterEach(() => {
  document.body.innerHTML = ''
  vi.restoreAllMocks()
})

beforeEach(() => {
  vi.clearAllMocks()
})

describe('WorktreeTasksPanel', () => {
  it('renders one card per task with status on each card', async () => {
    const { host, root } = await renderPanel()

    expect(host.querySelectorAll('[data-testid="task-card"]')).toHaveLength(3)
    expect(host.querySelectorAll('section')).toHaveLength(0)
    expect(host.textContent).toContain('Todo')
    expect(host.textContent).toContain('In Progress')
    expect(host.textContent).toContain('Done')
    expect(host.textContent).toContain('Todo task')
    expect(host.textContent).toContain('Active task')
    expect(host.textContent).toContain('Finished task')
    expect(host.querySelector('strong')).toBeNull()
    expect(host.textContent).not.toContain('Device01/FB_Scale')
    expect(host.textContent).toContain('Show 0 sessions')

    await act(async () => root.unmount())
  })

  it('creates a task from the focused add dialog', async () => {
    const { host, root, onChanged } = await renderPanel()
    await act(async () => {
      ;([...host.querySelectorAll('button')] as HTMLButtonElement[]).find(button => button.textContent?.includes('Add task'))!.click()
    })
    const dialog = document.body.querySelector('[data-slot="dialog-content"]') as HTMLElement
    const input = dialog.querySelector('input[aria-label="New task title"]') as HTMLInputElement
    expect(dialog.querySelectorAll('select[aria-label="New task device"]')).toHaveLength(1)
    expect(dialog.textContent).toContain('Main PLC')

    await act(async () => setInputValue(input, 'Add alarm handling'))
    expect((dialog.querySelector('button[type="submit"]') as HTMLButtonElement).disabled).toBe(true)
    await act(async () => setInputValue(dialog.querySelector('input[aria-label="New task goal"]') as HTMLInputElement, 'Add an alarm for overtemperature'))
    await act(async () => setInputValue(dialog.querySelector('textarea[aria-label="New task expected result"]') as HTMLTextAreaElement, 'PLC raises an alarm above the configured limit'))
    const device = dialog.querySelector('select[aria-label="New task device"]') as HTMLSelectElement
    await act(async () => { device.value = 'device-1'; device.dispatchEvent(new Event('change', { bubbles: true })) })
    await act(async () => {
      dialog.querySelector('button[type="submit"]')!.dispatchEvent(new MouseEvent('click', { bubbles: true }))
    })
    await act(async () => {})

    expect(vi.mocked(api.createGraphWorktreeTask)).toHaveBeenCalledWith('wb1', 'wt1', {
      title: 'Add alarm handling', deviceId: 'device-1', type: 'feature', intent: 'Add an alarm for overtemperature', expectedResult: 'PLC raises an alarm above the configured limit',
    })
    expect(onChanged).toHaveBeenCalled()
    expect(input.value).toBe('')

    await act(async () => root.unmount())
  })

  it('changes a task status through the status dropdown', async () => {
    const { host, root, onChanged } = await renderPanel()
    const trigger = host.querySelector('button[aria-label="Change status of Todo task"]') as HTMLButtonElement

    await act(async () => {
      trigger.dispatchEvent(new PointerEvent('pointerdown', { bubbles: true, button: 0, ctrlKey: false }))
      trigger.dispatchEvent(new MouseEvent('click', { bubbles: true }))
    })
    const item = Array.from(document.body.querySelectorAll<HTMLElement>('[role="menuitem"]'))
      .find(element => element.textContent?.trim() === 'In Progress')!
    await act(async () => {
      item.dispatchEvent(new MouseEvent('click', { bubbles: true }))
    })
    await act(async () => {})

    expect(vi.mocked(api.updateWorktreeTask)).toHaveBeenCalledWith('wb1', 'wt1', 't-todo', { status: 'inProgress' })
    expect(onChanged).toHaveBeenCalled()

    await act(async () => root.unmount())
  })

  it('edits title, details and element refs in the dialog', async () => {
    const { host, root, onChanged } = await renderPanel()

    await act(async () => {
      host.querySelector('button[aria-label="Edit task Todo task"]')!
        .dispatchEvent(new MouseEvent('click', { bubbles: true }))
    })

    const dialog = document.body.querySelector('[data-slot="dialog-content"]') as HTMLElement
    expect(dialog, 'edit dialog opens').toBeDefined()

    const titleInput = dialog.querySelector('input[aria-label="Task title"]') as HTMLInputElement
    const detailsInput = dialog.querySelector('textarea[aria-label="Task details"]') as HTMLTextAreaElement
    const refInput = dialog.querySelector('input[aria-label="Add element reference"]') as HTMLInputElement

    await act(async () => setInputValue(titleInput, 'Rework everything'))
    await act(async () => setInputValue(detailsInput, 'Step 1: export'))
    await act(async () => setInputValue(refInput, 'Device01/FB_Motor_Control'))
    await act(async () => {
      refInput.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true, cancelable: true }))
    })

    const save = Array.from(dialog.querySelectorAll('button')).find(button => button.textContent?.includes('Save task'))!
    await act(async () => save.dispatchEvent(new MouseEvent('click', { bubbles: true })))
    await act(async () => {})

    expect(vi.mocked(api.updateWorktreeTask)).toHaveBeenCalledWith('wb1', 'wt1', 't-todo', {
      title: 'Rework everything',
      details: 'Step 1: export',
      elementRefs: ['Device01/FB_Motor_Control'],
    })
    expect(onChanged).toHaveBeenCalled()

    await act(async () => root.unmount())
  })

  it('deletes a task after confirmation', async () => {
    const confirmMock = vi.fn(() => true)
    window.confirm = confirmMock as unknown as typeof window.confirm
    const { host, root, onChanged } = await renderPanel()

    await act(async () => {
      host.querySelector('button[aria-label="Delete task Active task"]')!
        .dispatchEvent(new MouseEvent('click', { bubbles: true }))
    })
    await act(async () => {})

    expect(confirmMock).toHaveBeenCalled()
    expect(vi.mocked(api.deleteWorktreeTask)).toHaveBeenCalledWith('wb1', 'wt1', 't-progress')
    expect(onChanged).toHaveBeenCalled()

    await act(async () => root.unmount())
  })

  it('shows an empty state when there are no tasks', async () => {
    const { host, root } = await renderPanel({ tasks: [] })

    expect(host.textContent).toContain('No tasks yet')

    await act(async () => root.unmount())
  })

  it('passes the selected task to its New chat callback', async () => {
    const onStartChat = vi.fn()
    const { host, root } = await renderPanel({ onStartChat })
    await act(async () => host.querySelector('button[aria-label="New chat for Todo task"]')!.click())
    expect(onStartChat).toHaveBeenCalledWith(tasks[0])
    await act(async () => root.unmount())
  })

  it('opens task details from the Detail action', async () => {
    const graphTask: api.EngineeringTask = {
      taskId: 'graph-1', workbenchId: 'wb1', scope: 'project', worktreeId: null,
      title: 'Graph-backed task', type: 'improvement', status: 'inProgress',
      priority: 1, intent: 'Improve traceability', expectedResult: 'Visible links',
      description: '', createdUtc: '2026-08-01T00:00:00Z', updatedUtc: '2026-08-01T00:00:00Z',
    }
    const onOpenTaskDetail = vi.fn()
    const { host, root } = await renderPanel({ tasks: [graphTask], onOpenTaskDetail })
    await act(async () => host.querySelector('button[aria-label="Open task detail Graph-backed task"]')!.click())
    expect(onOpenTaskDetail).toHaveBeenCalledWith(graphTask)
    await act(async () => root.unmount())
  })

  it('shows only sessions bound to the task with a relative last response time', async () => {
    const graphTask: api.EngineeringTask = {
      taskId: 'graph-1', workbenchId: 'wb1', scope: 'worktree', worktreeId: 'wt1', deviceId: 'device-1',
      title: 'Graph-backed task', type: 'improvement', status: 'inProgress',
      priority: 1, intent: 'Goal', expectedResult: 'Result', description: '',
      createdUtc: '2026-08-01T00:00:00Z', updatedUtc: '2026-08-01T00:00:00Z',
    }
    vi.mocked(api.listDeviceSessions).mockResolvedValueOnce([
      { sessionId: 's1', title: 'Motor alarm discussion', projectName: null, workbenchId: 'wb1', worktreeId: 'wt1', deviceId: 'device-1', createdAt: '2026-08-01T00:00:00Z', updatedAt: new Date(Date.now() - 60 * 60 * 1000).toISOString(), messageCount: 2, turnCount: 1, firstUserMessage: 'Help with the motor', taskId: 'graph-1' },
      { sessionId: 's2', title: 'Other task conversation', projectName: null, workbenchId: 'wb1', worktreeId: 'wt1', deviceId: 'device-1', createdAt: '2026-08-01T00:00:00Z', updatedAt: new Date().toISOString(), messageCount: 2, turnCount: 1, firstUserMessage: 'Other', taskId: 'other-task' },
    ])
    const onOpenTaskSession = vi.fn()
    const { host, root } = await renderPanel({ tasks: [graphTask], onOpenTaskSession })
    await act(async () => {})
    const disclosure = host.querySelector('button[aria-label="Show 1 sessions for Graph-backed task"]')!
    expect(disclosure.textContent).toContain('Show 1 sessions')
    await act(async () => disclosure.dispatchEvent(new MouseEvent('click', { bubbles: true })))
    expect(host.textContent).toContain('Motor alarm discussion')
    expect(host.textContent).toContain('1 hour ago')
    expect(host.textContent).not.toContain('Last response')
    expect(host.textContent).not.toContain('Other task conversation')
    const openButton = host.querySelector('button[aria-label="Open conversation Motor alarm discussion"]')!
    expect(openButton.textContent).toBe('Open')
    await act(async () => openButton.click())
    expect(onOpenTaskSession).toHaveBeenCalledWith(graphTask, 's1')
    await act(async () => host.querySelector('button[aria-label="List view"]')!.click())
    const row = host.querySelector('[data-testid="task-list-item"]') as HTMLTableRowElement
    expect(row.querySelectorAll('td')[3].textContent).toBe('1')
    await act(async () => root.unmount())
  })

  it('renders an exact graph task response description without legacy fields', async () => {
    const graphTask: api.EngineeringTask = {
      taskId: 'graph-1', workbenchId: 'wb1', scope: 'project', worktreeId: null,
      title: 'Graph-backed task', type: 'improvement', status: 'inProgress',
      priority: 1, intent: 'Improve traceability', expectedResult: 'Visible links',
      description: '**Graph** details', createdUtc: '2026-08-01T00:00:00Z', updatedUtc: '2026-08-01T00:00:00Z',
    }
    const { host, root } = await renderPanel({ tasks: [graphTask] })
    expect(host.textContent).toContain('Project scope')
    expect(host.textContent).toContain('Improvement')
    expect(host.textContent).not.toContain('Improve traceability')
    expect(host.textContent).not.toContain('Visible links')
    expect(host.textContent).toContain('Show 0 sessions')
    await act(async () => root.unmount())
  })

  it('switches between cards and list while keeping task actions available', async () => {
    const onStartChat = vi.fn()
    const { host, root } = await renderPanel({ onStartChat })
    expect(host.querySelectorAll('[data-testid="task-card"]')).toHaveLength(3)
    expect(host.querySelectorAll('[data-testid="task-list-item"]')).toHaveLength(0)

    await act(async () => host.querySelector('button[aria-label="List view"]')!.dispatchEvent(new MouseEvent('click', { bubbles: true })))
    expect(host.querySelectorAll('[data-testid="task-card"]')).toHaveLength(0)
    expect(host.querySelectorAll('[data-testid="task-list-item"]')).toHaveLength(3)
    expect(Array.from(host.querySelectorAll('thead th')).map(cell => cell.textContent)).toEqual(['Name', 'Status', 'Type', 'PLC', 'Sessions', 'Actions'])
    const firstRow = host.querySelector('[data-testid="task-list-item"]') as HTMLTableRowElement
    const name = firstRow.querySelector('th[scope="row"]')
    expect(name?.textContent).toBe('Todo task')
    expect(name?.querySelectorAll('*')).toHaveLength(0)
    expect(Array.from(firstRow.querySelectorAll('td')).map(cell => cell.textContent)).toEqual(['Todo', 'Feature', '—', '0', 'New chat'])
    expect(host.textContent).not.toContain('Worktree scope')
    expect(host.textContent).not.toContain('View brief')
    expect(host.textContent).not.toContain('Swap the sensor scaling')
    await act(async () => host.querySelector('button[aria-label="New chat for Todo task"]')!.dispatchEvent(new MouseEvent('click', { bubbles: true })))
    expect(onStartChat).toHaveBeenCalledWith(tasks[0])

    await act(async () => host.querySelector('button[aria-label="Card view"]')!.dispatchEvent(new MouseEvent('click', { bubbles: true })))
    expect(host.querySelectorAll('[data-testid="task-card"]')).toHaveLength(3)
    expect(host.querySelectorAll('[data-testid="task-list-item"]')).toHaveLength(0)
    await act(async () => root.unmount())
  })

  it('resizes adjacent list columns by dragging their accessible separator', async () => {
    const { host, root } = await renderPanel()
    await act(async () => host.querySelector('button[aria-label="List view"]')!.click())
    const columns = Array.from(host.querySelectorAll('col'))
    const widths = [150, 150, 100, 150, 80, 120]
    columns.forEach((column, index) => {
      vi.spyOn(column, 'getBoundingClientRect').mockReturnValue(new DOMRect(0, 0, widths[index], 20))
    })
    const handle = host.querySelector('[role="separator"][aria-label="Resize Name column"]')!
    await act(async () => handle.dispatchEvent(new PointerEvent('pointerdown', { bubbles: true, button: 0, clientX: 100, pointerId: 7 })))
    await act(async () => window.dispatchEvent(new PointerEvent('pointermove', { bubbles: true, clientX: 120, pointerId: 7 })))
    expect(columns[0].getAttribute('style')).toContain('170px')
    expect(columns[1].getAttribute('style')).toContain('130px')
    await act(async () => window.dispatchEvent(new PointerEvent('pointerup', { bubbles: true, clientX: 120, pointerId: 7 })))
    await act(async () => root.unmount())
  })
})
