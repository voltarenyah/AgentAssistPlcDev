// @vitest-environment happy-dom
import React, { act } from 'react'
import { createRoot } from 'react-dom/client'
import { afterEach, describe, expect, it, vi } from 'vitest'
import type { EngineeringTask } from '@/api/client'
import ChooseConversationTaskDialog from './ChooseConversationTaskDialog'

globalThis.IS_REACT_ACT_ENVIRONMENT = true

const task = (taskId: string, title: string): EngineeringTask => ({
  taskId, workbenchId: 'wb1', scope: 'worktree', worktreeId: 'wt1', title, type: 'feature', status: 'todo',
  priority: 0, intent: '', expectedResult: '', description: null, createdUtc: '', updatedUtc: '', deviceId: 'device-1',
})

const render = async (overrides: Partial<React.ComponentProps<typeof ChooseConversationTaskDialog>> = {}) => {
  const onChoose = vi.fn()
  const onClose = vi.fn()
  const host = document.createElement('div')
  document.body.appendChild(host)
  const root = createRoot(host)
  await act(async () => root.render(
    <ChooseConversationTaskDialog
      open
      tasks={[task('t1', 'First task'), task('t2', 'Second task')]}
      onChoose={onChoose}
      onClose={onClose}
      {...overrides}
    />,
  ))
  return { root, onChoose, onClose }
}

const dialog = () => document.body.querySelector('[data-slot="dialog-content"]') as HTMLElement

afterEach(() => { document.body.innerHTML = '' })

describe('ChooseConversationTaskDialog', () => {
  it('offers every task of the target and reports the chosen one', async () => {
    const { root, onChoose } = await render()
    const buttons = Array.from(dialog().querySelectorAll('[data-conversation-task]')) as HTMLButtonElement[]

    expect(buttons.map(button => button.getAttribute('data-conversation-task'))).toEqual(['t1', 't2'])
    expect(buttons.map(button => button.textContent?.trim())).toEqual(['First task', 'Second task'])
    await act(async () => buttons[1].dispatchEvent(new MouseEvent('click', { bubbles: true })))

    expect(onChoose).toHaveBeenCalledWith(expect.objectContaining({ taskId: 't2' }))
    await act(async () => root.unmount())
  })

  it('says why a task has to be chosen', async () => {
    const { root } = await render()
    // The reason is the section's own content rule, so the copy states it rather than asking blindly.
    expect(dialog().textContent).toContain('not listed in the navigator')
    await act(async () => root.unmount())
  })

  it('closes without choosing when cancelled', async () => {
    const { root, onChoose, onClose } = await render()
    const cancel = Array.from(dialog().querySelectorAll('button'))
      .find(button => button.textContent?.trim() === 'Cancel')!

    await act(async () => cancel.dispatchEvent(new MouseEvent('click', { bubbles: true })))

    expect(onChoose).not.toHaveBeenCalled()
    expect(onClose).toHaveBeenCalled()
    await act(async () => root.unmount())
  })

  it('renders nothing while closed', async () => {
    const { root } = await render({ open: false })
    expect(document.body.querySelector('[data-slot="dialog-content"]')).toBeNull()
    await act(async () => root.unmount())
  })
})
