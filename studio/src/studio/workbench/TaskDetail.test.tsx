// @vitest-environment happy-dom
import React, { act } from 'react'
import { createRoot } from 'react-dom/client'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import type { EngineeringTaskDetail } from '@/api/client'
import TaskDetail from './TaskDetail'

vi.mock('@/api/client', async importOriginal => ({
  ...(await importOriginal<typeof import('@/api/client')>()),
  listTaskSourceStages: vi.fn(async () => []),
  listWorktreeSourceStages: vi.fn(async () => []),
  listDeviceSourceObjects: vi.fn(async () => []),
  stageTaskSourceObject: vi.fn(),
  releaseTaskSourceObject: vi.fn(),
  // The Commits section reads the worktree history and the commit's own graph entity; both stay
  // empty here so this test covers the page's composition and not the section's own data.
  getVersionControlWorktreeLog: vi.fn(async () => ({ repoPath: '', commits: [] })),
  listDevices: vi.fn(async () => []),
  getGraphEntityDetail: vi.fn(async () => ({
    kind: 'gitCommit', id: 'commit-1', workbenchId: 'wb', worktreeId: 'wt', tasks: [], commits: [],
    sourceObjects: [], unresolvedFiles: [],
  })),
}))

globalThis.IS_REACT_ACT_ENVIRONMENT = true
const detail: EngineeringTaskDetail = {
  task: { taskId: 'task-1', workbenchId: 'wb', scope: 'worktree', worktreeId: 'wt', deviceId: 'device-hash', title: 'Motor update', type: 'feature', status: 'todo', priority: 1, intent: 'Improve', expectedResult: 'Safe', description: 'Plan', createdUtc: '', updatedUtc: '' },
  sessions: [{ id: 'session-1', edgeId: 'edge-session', provenance: 'default', isPrimary: true, title: 'Tune motor startup' }], commits: [{ id: 'abcdef1234567890', edgeId: 'edge-commit', provenance: 'evidence', isPrimary: false }], sourceObjects: [], svnRevisions: [{ id: 'r42', edgeId: 'edge-svn', provenance: 'manual', isPrimary: false }],
}
const render = async (props: React.ComponentProps<typeof TaskDetail>) => { const host = document.createElement('div'); document.body.appendChild(host); const root = createRoot(host); await act(async () => root.render(<TaskDetail {...props} />)); await act(async () => {}); return host }

describe('TaskDetail', () => {
  beforeEach(() => { document.body.innerHTML = '' })
  afterEach(() => { document.body.innerHTML = '' })

  it('shows the conversation name and navigates to linked records', async () => {
    const navigate = vi.fn(); const host = await render({ detail, deviceName: 'Line 4 conveyor PLC', onNavigate: navigate })
    expect(host.textContent).toContain('Task detail'); expect(host.textContent).toContain('Task fields'); expect(host.textContent).toContain('Sessions'); expect(host.textContent).toContain('Commits'); expect(host.textContent).toContain('Related records')
    // The remaining traceability edges are one group, so no section is named after them twice.
    expect(host.textContent).not.toContain('SVN revisions')
    expect(host.textContent).toContain('SVN revision')
    expect(host.textContent).toContain('Tune motor startup'); expect(host.textContent).toContain('Default link'); expect(host.textContent).toContain('Evidence-derived'); expect(host.textContent).toContain('Manual link')
    expect(host.querySelector<HTMLInputElement>('#task-device')?.readOnly).toBe(true)
    expect(host.querySelector<HTMLInputElement>('#task-device')?.value).toBe('Line 4 conveyor PLC')
    await act(async () => host.querySelector<HTMLButtonElement>('[aria-label="Open session Tune motor startup"]')?.click())
    expect(navigate).toHaveBeenCalledWith('session', 'session-1')
    // A commit has no destination on this page, so the Commits section is a disclosure and never a
    // clickable control that navigates nowhere.
    expect(host.querySelector('[aria-label="Open Commits abcdef1234567890"]')).toBeNull()
    await act(async () => host.querySelector<HTMLButtonElement>('[aria-label="Show details for commit abcdef1"]')?.click())
    expect(navigate).not.toHaveBeenCalledWith('commit', expect.anything())
    await act(async () => host.querySelector<HTMLButtonElement>('[aria-label="Open SVN revision r42"]')?.click())
    expect(navigate).toHaveBeenCalledWith('svnRevision', 'r42')
  })
  it('states the hardware target instead of calling a hardware task unbound', async () => {
    const host = await render({ detail: { ...detail, task: { ...detail.task, deviceId: null, targetKind: 'hardware' } } })
    expect(host.textContent).toContain('Target')
    expect(host.querySelector<HTMLInputElement>('#task-device')?.value).toBe('Hardware configuration')
    expect(host.textContent).toContain('binds no PLC device')
    // Its device is genuinely absent, but "not device-bound" would state the wrong reason.
    expect(host.textContent).not.toContain('Not device-bound')
  })
  it('offers the editable Source objects section for a device-bound worktree task', async () => {
    const host = await render({ detail, deviceName: 'Line 4 conveyor PLC' })
    const section = host.querySelector<HTMLElement>('[aria-label="Source objects"]')
    expect(section).not.toBeNull()
    expect(section?.textContent).toContain('No source objects staged yet.')
    expect(section?.querySelector('[aria-label="Add source object"]')).not.toBeNull()
  })
  it('explains why a project-scope task has no Source objects section instead of disabling controls', async () => {
    const host = await render({ detail: { ...detail, task: { ...detail.task, scope: 'project', worktreeId: null, deviceId: null } } })
    expect(host.querySelector('[aria-label="Source objects"]')).toBeNull()
    expect(host.textContent).toContain('Source objects are staged by a device-bound worktree task, and this is a project-scope task with no device to stage them from.')
  })
  it('starts a conversation for the task from the Sessions header', async () => {
    const start = vi.fn()
    const host = await render({ detail, deviceName: 'Line 4 conveyor PLC', onStartSession: start })
    const action = host.querySelector<HTMLButtonElement>('[aria-label="New chat for Motor update"]')
    expect(action).not.toBeNull()
    await act(async () => action?.click())
    expect(start).toHaveBeenCalledWith(detail.task)

    // A task that owns no conversation says how to start its first one, since the control lives here.
    document.body.innerHTML = ''
    const empty = await render({ detail: { ...detail, sessions: [] }, onStartSession: vi.fn() })
    expect(empty.textContent).toContain('Start one with New chat.')
  })
  it('offers no conversation to start for a task that cannot own one', async () => {
    const project = await render({
      detail: { ...detail, task: { ...detail.task, scope: 'project', worktreeId: null, deviceId: null } },
      onStartSession: vi.fn(),
    })
    expect(project.querySelector('[aria-label="New chat for Motor update"]')).toBeNull()
    document.body.innerHTML = ''
    const hardware = await render({
      detail: { ...detail, task: { ...detail.task, deviceId: null, targetKind: 'hardware' } },
      onStartSession: vi.fn(),
    })
    expect(hardware.querySelector('[aria-label="New chat for Motor update"]')).toBeNull()
  })
  it('carries the exact source-object identifier through navigation', async () => {
    const navigate = vi.fn()
    const host = await render({ detail: { ...detail, sourceObjects: [{ id: 'device-7/Blocks/Main', edgeId: 'edge-source', provenance: 'manual', isPrimary: false }] }, onNavigate: navigate })
    await act(async () => host.querySelector<HTMLButtonElement>('[aria-label="Open Source object device-7/Blocks/Main"]')?.click())
    expect(navigate).toHaveBeenCalledWith('sourceObject', 'device-7/Blocks/Main')
  })
  it('saves editable task fields while keeping device binding read-only', async () => {
    const save = vi.fn(async () => undefined)
    const host = await render({ detail, onSave: save })
    const title = host.querySelector<HTMLInputElement>('#task-title')!
    await act(async () => {
      Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value')?.set?.call(title, 'Updated motor task')
      title.dispatchEvent(new Event('input', { bubbles: true }))
    })
    const saveButton = Array.from(host.querySelectorAll('button')).find(button => button.textContent?.includes('Save changes'))
    await act(async () => saveButton?.click())
    expect(save).toHaveBeenCalledWith(expect.objectContaining({ title: 'Updated motor task' }))
    expect(save.mock.calls[0]?.[0]).not.toHaveProperty('deviceId')
    expect(host.querySelector<HTMLInputElement>('#task-device')?.readOnly).toBe(true)
  })
  it('makes loading and failed-query states explicit and retries', async () => {
    const retry = vi.fn(); const loading = await render({ detail: null, loading: true }); expect(loading.textContent).toContain('Loading task details')
    document.body.innerHTML = ''; const failed = await render({ detail: null, error: 'network unavailable', onRetry: retry }); expect(failed.textContent).toContain('network unavailable'); await act(async () => failed.querySelector('button')?.click()); expect(retry).toHaveBeenCalledOnce()
  })
  it('labels manual relationships with an accessible remove action', async () => {
    const remove = vi.fn(); const host = await render({ detail, onRemove: remove }); const button = host.querySelector<HTMLButtonElement>('[aria-label="Remove SVN revision r42"]'); expect(button).not.toBeNull(); await act(async () => button?.click()); expect(remove).toHaveBeenCalledWith('svnRevision', detail.svnRevisions[0])
  })
  it('labels an automatic relation and lets it be cleared, while a default link offers no removal (AC-019)', async () => {
    const remove = vi.fn()
    const sessions: EngineeringTaskDetail['sessions'] = [
      { id: 'session-auto', edgeId: 'edge-auto', provenance: 'auto', isPrimary: true, title: 'Created here' },
      { id: 'session-default', edgeId: 'edge-default', provenance: 'default', isPrimary: false, title: 'Related by default' },
      { id: 'session-manual', edgeId: 'edge-manual', provenance: 'manual', isPrimary: false, title: 'Linked by hand' },
    ]
    const host = await render({ detail: { ...detail, sessions }, onRemove: remove })

    // An automatic relation says the conversation created the task, instead of reading as an
    // unclassified link the way an unknown provenance does.
    expect(host.textContent).toContain('Created by this conversation')
    expect(host.textContent).not.toContain('Unassigned')

    // It is clearable exactly like a manual link — a relation the conversation made must have a way
    // back — while a default link is cleared from the conversation's own picker instead.
    const auto = host.querySelector<HTMLButtonElement>('[aria-label="Remove Sessions session-auto"]')
    expect(auto).not.toBeNull()
    expect(host.querySelector('[aria-label="Remove Sessions session-manual"]')).not.toBeNull()
    expect(host.querySelector('[aria-label="Remove Sessions session-default"]')).toBeNull()

    await act(async () => auto?.click())
    expect(remove).toHaveBeenCalledWith('session',
      expect.objectContaining({ id: 'session-auto', edgeId: 'edge-auto', provenance: 'auto', isPrimary: true }))
  })
})
