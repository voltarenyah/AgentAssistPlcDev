// @vitest-environment happy-dom
import React, { act } from 'react'
import { createRoot } from 'react-dom/client'
import { describe, expect, it, vi } from 'vitest'
import type { EngineeringTaskDetail } from '@/api/client'
import TaskDetail from './TaskDetail'

globalThis.IS_REACT_ACT_ENVIRONMENT = true
const detail: EngineeringTaskDetail = {
  task: { taskId: 'task-1', workbenchId: 'wb', scope: 'worktree', worktreeId: 'wt', title: 'Motor update', type: 'feature', status: 'todo', priority: 1, intent: 'Improve', expectedResult: 'Safe', description: 'Plan', createdUtc: '', updatedUtc: '' },
  sessions: [{ id: 'session-1', edgeId: 'edge-session', provenance: 'default', isPrimary: true }], commits: [{ id: 'commit-1', edgeId: 'edge-commit', provenance: 'evidence', isPrimary: false }], sourceObjects: [], svnRevisions: [{ id: 'r42', edgeId: 'edge-svn', provenance: 'manual', isPrimary: false }],
}
const render = async (props: React.ComponentProps<typeof TaskDetail>) => { const host = document.createElement('div'); document.body.appendChild(host); const root = createRoot(host); await act(async () => root.render(<TaskDetail {...props} />)); return host }

describe('TaskDetail', () => {
  it('renders all categories independently with provenance and navigation', async () => {
    const navigate = vi.fn(); const host = await render({ detail, onNavigate: navigate })
    expect(host.textContent).toContain('Sessions'); expect(host.textContent).toContain('Commits'); expect(host.textContent).toContain('Source objects'); expect(host.textContent).toContain('SVN revisions')
    expect(host.textContent).toContain('Default'); expect(host.textContent).toContain('Evidence-derived'); expect(host.textContent).toContain('Manual'); expect(host.textContent).toContain('No linked source objects yet.')
    await act(async () => host.querySelector<HTMLButtonElement>('[aria-label="Open Sessions session-1"]')?.click())
    expect(navigate).toHaveBeenCalledWith('session', 'session-1')
    await act(async () => host.querySelector<HTMLButtonElement>('[aria-label="Open Commits commit-1"]')?.click())
    expect(navigate).toHaveBeenCalledWith('commit', 'commit-1')
    await act(async () => host.querySelector<HTMLButtonElement>('[aria-label="Open SVN revisions r42"]')?.click())
    expect(navigate).toHaveBeenCalledWith('svnRevision', 'r42')
  })
  it('carries the exact source-object identifier through navigation', async () => {
    const navigate = vi.fn()
    const host = await render({ detail: { ...detail, sourceObjects: [{ id: 'device-7/Blocks/Main', edgeId: 'edge-source', provenance: 'manual', isPrimary: false }] }, onNavigate: navigate })
    await act(async () => host.querySelector<HTMLButtonElement>('[aria-label="Open Source objects device-7/Blocks/Main"]')?.click())
    expect(navigate).toHaveBeenCalledWith('sourceObject', 'device-7/Blocks/Main')
  })
  it('makes loading and failed-query states explicit and retries', async () => {
    const retry = vi.fn(); const loading = await render({ detail: null, loading: true }); expect(loading.textContent).toContain('Loading task traceability')
    document.body.innerHTML = ''; const failed = await render({ detail: null, error: 'network unavailable', onRetry: retry }); expect(failed.textContent).toContain('network unavailable'); await act(async () => failed.querySelector('button')?.click()); expect(retry).toHaveBeenCalledOnce()
  })
  it('labels manual relationships with an accessible remove action', async () => {
    const remove = vi.fn(); const host = await render({ detail, onRemove: remove }); const button = host.querySelector<HTMLButtonElement>('[aria-label="Remove SVN revisions r42"]'); expect(button).not.toBeNull(); await act(async () => button?.click()); expect(remove).toHaveBeenCalledWith('svnRevision', detail.svnRevisions[0])
  })
})
