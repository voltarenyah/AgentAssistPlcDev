// @vitest-environment happy-dom
import React, { act } from 'react'
import { createRoot } from 'react-dom/client'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import * as api from '@/api/client'
import WorktreeTasksPanel from './WorktreeTasksPanel'

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
    createWorktreeTask: vi.fn(async (_wb: string, _wt: string, body: { title: string }) =>
      task({ taskId: 't-new', title: body.title })),
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
  await act(async () => root.render(
    <WorktreeTasksPanel
      workbenchId="wb1"
      worktreeId="wt1"
      tasks={tasks}
      loading={false}
      error={null}
      onChanged={onChanged}
      {...overrides}
    />,
  ))
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
  it('groups tasks by status with per-group counts', async () => {
    const { host, root } = await renderPanel()

    expect(host.textContent).toContain('Todo')
    expect(host.textContent).toContain('In Progress')
    expect(host.textContent).toContain('Done')
    expect(host.textContent).toContain('Todo task')
    expect(host.textContent).toContain('Active task')
    expect(host.textContent).toContain('Finished task')
    // Markdown details render in view mode, refs show as chips.
    expect(host.querySelector('strong')?.textContent).toBe('Swap')
    expect(host.textContent).toContain('Device01/FB_Scale')

    await act(async () => root.unmount())
  })

  it('creates a task from the inline add input', async () => {
    const { host, root, onChanged } = await renderPanel()
    const input = host.querySelector('input[aria-label="New task title"]') as HTMLInputElement

    await act(async () => setInputValue(input, 'Add alarm handling'))
    await act(async () => {
      input.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true, cancelable: true }))
    })
    await act(async () => {})

    expect(vi.mocked(api.createWorktreeTask)).toHaveBeenCalledWith('wb1', 'wt1', { title: 'Add alarm handling' })
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
})
