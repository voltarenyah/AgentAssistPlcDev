// @vitest-environment happy-dom
import React, { act } from 'react'
import { createRoot } from 'react-dom/client'
import { afterEach, describe, expect, it } from 'vitest'
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
  onCreateWorkbench: () => {}, onCreateWorktree: () => {}, onOpenWorkbench: () => {}, onOpenWorktree: () => {}, onInspectWorkbench: () => {}, onInspectWorktree: () => {}, onArchiveWorktree: () => {}, onRefresh: () => {}, onSelectWorkbench: () => {}, onSelectWorktree: () => {}, onSelectDevice: () => {}, onSelectHardware: () => {}, onReloadHardware: () => {}, onCompareHardware: () => {}, onDeleteWorkbench: () => {}, onDeleteWorktree: () => {}, onMergeWorktree: () => {}, onOpenDevice: () => {}, onUpgradeDevice: () => {}, onInspectDevice: () => {}, onCompareDevice: () => {}, onRebuildDevice: () => {}, onUpdateKnowledge: () => {}, onRebuildKnowledge: () => {},
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
})
