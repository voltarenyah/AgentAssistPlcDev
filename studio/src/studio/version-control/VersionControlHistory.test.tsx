// @vitest-environment happy-dom
import React, { act } from 'react'
import { createRoot } from 'react-dom/client'
import { afterEach, describe, expect, it, vi } from 'vitest'
import * as api from '@/api/client'
import VersionControlHistory, { type VcTimelineItem } from './VersionControlHistory'

globalThis.IS_REACT_ACT_ENVIRONMENT = true

const commit = (overrides: Partial<Extract<VcTimelineItem, { kind: 'commit' }>> = {}): VcTimelineItem => ({
  kind: 'commit',
  sha: 'abcdef1234567890',
  message: 'Validate Main block',
  author: 'Ansel',
  timestamp: '2026-08-04T08:00:00.000Z',
  files: ['devices/PLC_1/source/Blocks/Main.xml'],
  tiaChecksum: 'B3 35 56 49',
  svnRevision: 4,
  validationState: 'Validated',
  ...overrides,
})

const savepoint = (overrides: Partial<Extract<VcTimelineItem, { kind: 'savepoint' }>> = {}): VcTimelineItem => ({
  kind: 'savepoint',
  revision: 4,
  message: 'before IP change',
  author: 'PLC Assistant',
  timestamp: '2026-08-04T08:01:00.000Z',
  tiaChecksum: 'B3 35 56 49',
  gitCommitSha: 'abcdef1234567890',
  ...overrides,
})

const render = async (items: VcTimelineItem[]) => {
  const host = document.createElement('div')
  document.body.appendChild(host)
  const root = createRoot(host)
  await act(async () => {
    root.render(
      <VersionControlHistory
        workbenchId="wb-1"
        worktreeId="wt-1"
        branch="master"
        items={items}
        loading={false}
      />,
    )
  })
  return { host, root }
}

const click = async (element: Element) => {
  await act(async () => element.dispatchEvent(new MouseEvent('click', { bubbles: true })))
}

afterEach(() => {
  document.body.innerHTML = ''
  vi.restoreAllMocks()
})

describe('VersionControlHistory', () => {
  it('renders commits and savepoints on one timeline', async () => {
    const { host } = await render([commit(), savepoint()])

    expect(host.querySelector('[data-testid="commit-abcdef1"]')?.textContent).toContain('Validate Main block')
    expect(host.querySelector('[data-testid="savepoint-r4"]')?.textContent).toContain('before IP change')
    // The head commit carries the branch ref chip.
    expect(host.querySelector('[data-testid="commit-abcdef1"]')?.textContent).toContain('master')
  })

  it('expands a commit to show checksum and changed files', async () => {
    const { host } = await render([commit()])

    await click(host.querySelector('[data-testid="commit-abcdef1"]')!)

    const detail = host.querySelector('[data-testid="timeline-detail"]')!
    expect(detail.textContent).not.toContain('TIA validated')
    expect(detail.textContent).toContain('r4')
    expect(detail.textContent).toContain('B3 35 56 49')
    expect(detail.textContent).toContain('devices/PLC_1/source/Blocks/Main.xml')
  })

  it('creates a rollback feature from selected files instead of restoring master', async () => {
    const create = vi.spyOn(api, 'createRollbackFeature').mockResolvedValue({
      worktreeId: 'rollback-main',
      name: 'rollback-main',
    })
    const { host } = await render([commit()])

    await click(host.querySelector('[data-testid="commit-abcdef1"]')!)
    await click(host.querySelector('[data-testid="object-Main.xml"]')!)

    const nameInput = host.querySelector('input[aria-label="Rollback feature name"]') as HTMLInputElement
    expect(nameInput.value).toBe('rollback-abcdef1')

    await click(host.querySelector('[data-testid="create-rollback-feature"]')!)

    expect(create).toHaveBeenCalledWith(
      'wb-1',
      'abcdef1234567890',
      ['devices/PLC_1/source/Blocks/Main.xml'],
      'rollback-abcdef1',
    )
  })

  it('keeps multiple timeline items expanded at the same time', async () => {
    const { host } = await render([
      commit(),
      commit({ sha: '1234567abcdef890', message: 'Adjust alarms' }),
      savepoint(),
    ])

    await click(host.querySelector('[data-testid="commit-abcdef1"]')!)
    await click(host.querySelector('[data-testid="commit-1234567"]')!)
    await click(host.querySelector('[data-testid="savepoint-r4"]')!)

    // Expanding one item no longer folds the others.
    expect(host.querySelectorAll('[data-testid="timeline-detail"]').length).toBe(3)

    // Clicking an open item folds just that one.
    await click(host.querySelector('[data-testid="commit-1234567"]')!)
    expect(host.querySelectorAll('[data-testid="timeline-detail"]').length).toBe(2)
  })

  it('exports a savepoint project through the right-click menu', async () => {
    const restore = vi.spyOn(api, 'restoreTiaProject').mockResolvedValue({
      gitCommit: 'abcdef1234567890',
      svnUrl: 'svn://repo',
      svnRevision: 4,
      restoredDirectory: 'C:/export/B3355649',
      restoredProjectPath: null,
    })
    const { host } = await render([savepoint()])

    await act(async () => {
      host.querySelector('[data-testid="savepoint-r4"]')!.dispatchEvent(new MouseEvent('contextmenu', { bubbles: true, clientX: 40, clientY: 60 }))
    })

    const menu = document.querySelector('[data-testid="savepoint-menu"]')
    expect(menu?.textContent).toContain('Export saved project')

    await click(menu!.querySelector('[data-testid="savepoint-export"]')!)

    expect(restore).toHaveBeenCalledWith('wb-1', 'wt-1', 'abcdef1234567890')
    expect(document.querySelector('[data-testid="savepoint-menu"]')).toBeNull()
  })
})
