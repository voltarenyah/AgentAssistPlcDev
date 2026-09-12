// @vitest-environment happy-dom
import React, { act } from 'react'
import { createRoot } from 'react-dom/client'
import { afterEach, describe, expect, it, vi } from 'vitest'
import type { EngineeringTask } from '@/api/client'
import { ActiveTaskSelector } from './WorktreeTasksPanel'

globalThis.IS_REACT_ACT_ENVIRONMENT = true

const task = (id: string, scope: 'project' | 'worktree', worktreeId: string | null = null): EngineeringTask => ({
  taskId: id, workbenchId: 'wb1', scope, worktreeId, title: id, type: 'feature', status: 'todo',
  details: null, elementRefs: [], createdUtc: '2026-01-01T00:00:00Z', updatedUtc: '2026-01-01T00:00:00Z',
  doneUtc: null, priority: 0, intent: 'intent', expectedResult: 'result',
})

afterEach(() => { document.body.innerHTML = '' })

describe('ActiveTaskSelector', () => {
  it('supports keyboard selection and clear while excluding another worktree', async () => {
    const host = document.createElement('div'); document.body.appendChild(host)
    const root = createRoot(host)
    const onChange = vi.fn(async () => {})
    await act(async () => root.render(<ActiveTaskSelector
      tasks={[task('project', 'project'), task('mine', 'worktree', 'wt1'), task('other', 'worktree', 'wt2')]}
      activeTask={task('project', 'project')}
      worktreeId="wt1"
      onChange={onChange}
    />))
    const select = host.querySelector('select[aria-label="Active task"]') as HTMLSelectElement
    expect(select.options).toHaveLength(3)
    expect(Array.from(select.options).some(option => option.value === 'other')).toBe(false)
    await act(async () => { select.value = 'mine'; select.dispatchEvent(new Event('change', { bubbles: true })) })
    expect(onChange).toHaveBeenCalledWith('mine')
    await act(async () => { (host.querySelector('button') as HTMLButtonElement).click() })
    expect(onChange).toHaveBeenCalledWith(null)
    await act(async () => root.unmount())
  })

  it('keeps the visible task and exposes recovery when the server rejects a switch', async () => {
    const host = document.createElement('div'); document.body.appendChild(host)
    const root = createRoot(host)
    await act(async () => root.render(<ActiveTaskSelector
      tasks={[task('mine', 'worktree', 'wt1')]}
      activeTask={task('mine', 'worktree', 'wt1')}
      worktreeId="wt1"
      onChange={async () => { throw new Error('server unavailable') }}
    />))
    const select = host.querySelector('select') as HTMLSelectElement
    await act(async () => { select.value = ''; select.dispatchEvent(new Event('change', { bubbles: true })) })
    expect(host.textContent).toContain('server unavailable')
    expect(select.value).toBe('mine')
    expect(host.querySelector('[role="alert"]')).toBeTruthy()
    await act(async () => root.unmount())
  })
})
