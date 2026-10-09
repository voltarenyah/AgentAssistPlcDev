// @vitest-environment happy-dom
import React, { act } from 'react'
import { createRoot } from 'react-dom/client'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import * as api from '@/api/client'
import WorktreeSessionsPanel, { type SessionViewMode } from './WorktreeSessionsPanel'

globalThis.IS_REACT_ACT_ENVIRONMENT = true

const conversation = (overrides: Partial<api.ChatSessionInfo> & { sessionId: string }): api.ChatSessionInfo => ({
  title: null, workbenchId: 'wb1', worktreeId: 'wt1', deviceId: 'device-1',
  createdAt: '2026-10-01T00:00:00Z', updatedAt: '2026-10-01T00:00:00Z',
  messageCount: 2, turnCount: 1, firstUserMessage: null, taskId: null,
  ...overrides,
})

const relation = (taskId: string, overrides: Partial<api.SessionTaskRelation> = {}): api.SessionTaskRelation => ({
  taskId, edgeId: `edge-${taskId}`, provenance: 'manual', isPrimary: false, ...overrides,
})

const engineeringTask = (overrides: Partial<api.EngineeringTask> & { taskId: string; title: string }): api.EngineeringTask => ({
  workbenchId: 'wb1', scope: 'worktree', worktreeId: 'wt1', type: 'feature', status: 'todo',
  priority: 0, intent: 'Intent', expectedResult: 'Result', description: null, createdUtc: '', updatedUtc: '',
  ...overrides,
})

const tasks: api.EngineeringTask[] = [
  engineeringTask({ taskId: 'task-device', title: 'Valve interlock', deviceId: 'device-1' }),
  engineeringTask({ taskId: 'task-other', title: 'Second task', deviceId: 'device-1' }),
  // A task that cannot own a conversation is never offered in the picker.
  engineeringTask({ taskId: 'task-hardware', title: 'Hardware task', deviceId: null, targetKind: 'hardware' }),
]

const sessions: api.ChatSessionInfo[] = [
  conversation({
    sessionId: 'session-bound', title: 'Interlock review', taskId: 'task-device',
    taskProvenance: 'manual', taskRelations: [relation('task-device', { isPrimary: true })],
    turnCount: 3, messageCount: 7,
  }),
  conversation({ sessionId: 'session-adhoc', title: 'Ad-hoc question', firstUserMessage: 'Why does FB_Motor reset?' }),
  // The worktree list is worktree-wide, so it also holds a conversation of another device.
  conversation({ sessionId: 'session-elsewhere', title: 'Other device question', deviceId: 'device-2' }),
]

vi.mock('@/api/client', async importOriginal => {
  const actual = await importOriginal<typeof import('@/api/client')>()
  return {
    ...actual,
    listWorktreeSessions: vi.fn(async () => sessions),
    listDevices: vi.fn(async () => [
      { deviceId: 'device-1', plcName: 'Main PLC' },
      { deviceId: 'device-2', plcName: 'Second PLC' },
    ]),
  }
})

type PanelProps = React.ComponentProps<typeof WorktreeSessionsPanel>

const renderPanel = async (overrides: Partial<PanelProps> = {}) => {
  const host = document.createElement('div')
  document.body.appendChild(host)
  const root = createRoot(host)
  function Harness() {
    const [viewMode, setViewMode] = React.useState<SessionViewMode>('cards')
    return <WorktreeSessionsPanel
      workbenchId="wb1"
      worktreeId="wt1"
      tasks={tasks}
      viewMode={viewMode}
      onViewModeChange={setViewMode}
      {...overrides}
    />
  }
  await act(async () => root.render(<Harness />))
  return { host, root }
}

const setInputValue = (input: HTMLInputElement, value: string) => {
  const setter = Object.getOwnPropertyDescriptor(window.HTMLInputElement.prototype, 'value')!.set!
  setter.call(input, value)
  input.dispatchEvent(new Event('input', { bubbles: true }))
}

const clickButton = async (host: HTMLElement, label: string) => {
  const button = [...host.querySelectorAll('button')].find(candidate => candidate.getAttribute('aria-label') === label)
    ?? [...host.querySelectorAll('button')].find(candidate => candidate.textContent?.includes(label))
  expect(button, `button ${label}`).toBeDefined()
  await act(async () => button!.click())
}

const openRowMenu = async (trigger: HTMLButtonElement) => {
  await act(async () => {
    trigger.dispatchEvent(new PointerEvent('pointerdown', { bubbles: true, button: 0, ctrlKey: false }))
    trigger.dispatchEvent(new MouseEvent('click', { bubbles: true }))
  })
  return Array.from(document.body.querySelectorAll<HTMLElement>('[role="menuitem"]'))
}

const rowMenuTrigger = (host: HTMLElement, title: string) =>
  host.querySelector<HTMLButtonElement>(`button[aria-label="Conversation actions ${title}"]`)!

afterEach(() => {
  document.body.innerHTML = ''
  vi.restoreAllMocks()
})

beforeEach(() => {
  vi.clearAllMocks()
  vi.mocked(api.listWorktreeSessions).mockResolvedValue(sessions)
})

describe('WorktreeSessionsPanel', () => {
  it('lists every conversation of the worktree as a card, naming its tasks and device', async () => {
    const { host, root } = await renderPanel()

    expect(host.querySelectorAll('[data-testid="session-card"]')).toHaveLength(3)
    expect(host.textContent).toContain('Interlock review')
    expect(host.textContent).toContain('Valve interlock')
    expect(host.textContent).toContain('PLC · Main PLC')
    expect(host.textContent).toContain('3 turns · 7 messages')
    // A conversation no task owns says so rather than showing an empty set of badges.
    expect(host.textContent).toContain('No task')
    // The list is the worktree's, so a conversation of another device is listed too.
    expect(host.textContent).toContain('Other device question')
    expect(host.textContent).toContain('PLC · Second PLC')
    expect(host.textContent).toContain('3 of 3 conversations')

    await act(async () => root.unmount())
  })

  it('offers the same conversations as a list when the list view is chosen', async () => {
    const { host, root } = await renderPanel()

    await clickButton(host, 'List view')

    expect(host.querySelectorAll('[data-testid="session-card"]')).toHaveLength(0)
    const rows = host.querySelectorAll('[data-testid="session-list-item"]')
    expect(rows).toHaveLength(3)
    expect(rows[0].textContent).toContain('Interlock review')
    expect(rows[0].textContent).toContain('Valve interlock')

    await act(async () => root.unmount())
  })

  it('searches the conversations it is showing by title and by what was asked', async () => {
    const { host, root } = await renderPanel()
    const search = host.querySelector<HTMLInputElement>('input[aria-label="Search conversations"]')!

    await act(async () => setInputValue(search, 'other device'))
    expect(host.querySelectorAll('[data-testid="session-card"]')).toHaveLength(1)
    expect(host.textContent).toContain('Other device question')
    expect(host.textContent).toContain('1 of 3 conversations')

    // A conversation's first user message is what it is searched by when it has no title of its own.
    await act(async () => setInputValue(search, 'fb_motor'))
    expect(host.querySelectorAll('[data-testid="session-card"]')).toHaveLength(1)
    expect(host.textContent).toContain('Ad-hoc question')

    await act(async () => setInputValue(search, 'nothing matches this'))
    expect(host.querySelectorAll('[data-testid="session-card"]')).toHaveLength(0)
    expect(host.textContent).toContain('No conversation matches “nothing matches this”.')

    await act(async () => root.unmount())
  })

  it('renames a conversation from its row menu, and re-reads the list afterwards', async () => {
    const onRenameSession = vi.fn(async () => {})
    const { host, root } = await renderPanel({ onRenameSession })

    const items = await openRowMenu(rowMenuTrigger(host, 'Interlock review'))
    expect(items.map(item => item.textContent?.trim())).toEqual([
      'Open conversation', 'Rename conversation', 'Export conversation', 'Tasks…', 'Delete conversation',
    ])
    await act(async () => items.find(item => item.textContent?.includes('Rename conversation'))!.click())

    const dialog = document.body.querySelector('[data-slot="dialog-content"]') as HTMLElement
    const input = dialog.querySelector<HTMLInputElement>('input[aria-label="Conversation title"]')!
    expect(input.value).toBe('Interlock review')
    await act(async () => setInputValue(input, 'Interlock review (closed)'))
    await act(async () => (dialog.querySelector('button[type="submit"]') as HTMLButtonElement).click())

    expect(onRenameSession).toHaveBeenCalledWith(
      expect.objectContaining({ sessionId: 'session-bound' }), 'Interlock review (closed)')
    // The tab re-reads the list once the operation settles, so the row shows the new name next.
    expect(vi.mocked(api.listWorktreeSessions).mock.calls.length).toBeGreaterThan(1)

    await act(async () => root.unmount())
  })

  it('applies a conversation\'s whole relation set from the row menu picker', async () => {
    const onSetSessionTasks = vi.fn(async () => {})
    const { host, root } = await renderPanel({ onSetSessionTasks })

    const items = await openRowMenu(rowMenuTrigger(host, 'Ad-hoc question'))
    await act(async () => items.find(item => item.textContent?.includes('Tasks…'))!.click())

    // The picker offers every task that can own a conversation, and never the untargeted one.
    const options = Array.from(document.body.querySelectorAll<HTMLElement>('[role="option"]'))
      .map(option => option.textContent?.trim())
    expect(options).toEqual(['Valve interlock', 'Second task'])
    expect(document.body.textContent).not.toContain('Hardware task')

    const first = document.body.querySelector<HTMLElement>('[role="option"]')!
    await act(async () => first.dispatchEvent(new MouseEvent('click', { bubbles: true })))
    const apply = Array.from(document.body.querySelectorAll('button')).find(button => button.textContent?.trim() === 'Apply')!
    await act(async () => apply.dispatchEvent(new MouseEvent('click', { bubbles: true })))

    expect(onSetSessionTasks).toHaveBeenCalledWith(
      expect.objectContaining({ sessionId: 'session-adhoc' }), ['task-device'], undefined)

    await act(async () => root.unmount())
  })

  it('asks before deleting a conversation, and only deletes when confirmed', async () => {
    const onDeleteSession = vi.fn(async () => {})
    // happy-dom has no window.confirm; the browser does, and the row uses it to ask.
    const confirm = vi.fn(() => false)
    vi.stubGlobal('confirm', confirm)
    const { host, root } = await renderPanel({ onDeleteSession })

    const items = await openRowMenu(rowMenuTrigger(host, 'Interlock review'))
    await act(async () => items.find(item => item.textContent?.includes('Delete conversation'))!.click())
    // A related conversation loses its links, so the confirmation says so.
    expect(confirm.mock.calls[0][0]).toContain('links to the tasks it is related to are lost')
    expect(onDeleteSession).not.toHaveBeenCalled()

    confirm.mockReturnValue(true)
    const confirmed = await openRowMenu(rowMenuTrigger(host, 'Interlock review'))
    await act(async () => confirmed.find(item => item.textContent?.includes('Delete conversation'))!.click())
    expect(onDeleteSession).toHaveBeenCalledWith(expect.objectContaining({ sessionId: 'session-bound' }))

    await act(async () => root.unmount())
  })

  it('says so when the worktree holds no conversation at all', async () => {
    vi.mocked(api.listWorktreeSessions).mockResolvedValue([])
    const { host, root } = await renderPanel()

    expect(host.querySelectorAll('[data-testid="session-card"]')).toHaveLength(0)
    expect(host.textContent).toContain('This worktree has no conversations yet.')

    await act(async () => root.unmount())
  })

  it('reports a conversation list that could not be read instead of an empty one', async () => {
    vi.mocked(api.listWorktreeSessions).mockRejectedValue(new Error('graph is unavailable'))
    const { host, root } = await renderPanel()

    expect(host.textContent).toContain('Conversations could not be loaded: graph is unavailable')

    await act(async () => root.unmount())
  })
})
