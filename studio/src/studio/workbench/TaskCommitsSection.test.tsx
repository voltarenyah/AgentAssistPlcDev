// @vitest-environment happy-dom
import React, { act } from 'react'
import { createRoot } from 'react-dom/client'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import type { DeviceSnapshot, EngineeringGraphEntityDetail, SourceObjectInfo, VcLogResult } from '@/api/client'
import TaskCommitsSection from './TaskCommitsSection'

vi.mock('@/api/client', () => ({
  getVersionControlWorktreeLog: vi.fn(),
  listDevices: vi.fn(),
  getDeviceInfo: vi.fn(),
  getGraphEntityDetail: vi.fn(),
}))

import * as api from '@/api/client'

globalThis.IS_REACT_ACT_ENVIRONMENT = true

const sha = 'abcdef1234567890abcdef1234567890abcdef12'
const short = 'abcdef1'

const sourceObject = (id: string, name: string, category: string, relativePath: string): SourceObjectInfo => ({
  id, name, number: null, category, programmingLanguage: null, groupPath: null, relativePath,
  contentHash: null, isKnowHowProtected: null, modifiedDate: null, status: null,
})

const history = (): VcLogResult => ({
  repoPath: 'C:/repo',
  commits: [{
    sha,
    author: 'Ada Lovelace',
    message: 'Tune the conveyor motor',
    timestamp: '2026-09-30T10:00:00Z',
    files: ['devices/plc/source/blocks/Main.xml'],
    validationState: 'Validated',
    evidenceKind: null,
  }],
})

const commitEntity = (sourceObjects: EngineeringGraphEntityDetail['sourceObjects'], unresolvedFiles: string[]): EngineeringGraphEntityDetail => ({
  kind: 'gitCommit', id: sha, workbenchId: 'wb1', worktreeId: 'wt1', tasks: [], commits: [], sourceObjects, unresolvedFiles,
})

const sourceEdge = (id: string) => ({ id, edgeId: `edge-${id}`, provenance: 'evidence', isPrimary: false })

const commits = [{ id: sha, edgeId: 'edge-commit', provenance: 'default', isPrimary: true }]

const render = async (element: React.ReactNode) => {
  const host = document.createElement('div')
  document.body.appendChild(host)
  const root = createRoot(host)
  await act(async () => root.render(element))
  await act(async () => {})
  return { host, root }
}

const section = (props: Partial<React.ComponentProps<typeof TaskCommitsSection>> = {}) => (
  <TaskCommitsSection workbenchId="wb1" worktreeId="wt1" commits={commits} {...props} />
)

const expand = async (host: HTMLElement, label = `Show details for commit ${short}`) => {
  await act(async () => host.querySelector<HTMLButtonElement>(`[aria-label="${label}"]`)?.click())
  await act(async () => {})
}

describe('TaskCommitsSection', () => {
  beforeEach(() => {
    document.body.innerHTML = ''
    vi.mocked(api.getVersionControlWorktreeLog).mockResolvedValue(history())
    vi.mocked(api.listDevices).mockResolvedValue([{ deviceId: 'dev-1', plcName: 'PLC_1' }])
    vi.mocked(api.getDeviceInfo).mockResolvedValue({
      sourceObjects: [sourceObject('block-main', 'Main', 'Function block', 'devices/plc/source/blocks/Main.xml')],
    } as unknown as DeviceSnapshot)
    vi.mocked(api.getGraphEntityDetail).mockResolvedValue(commitEntity([sourceEdge('dev-1:block-main')], ['devices/plc/source/blocks/Removed.xml']))
  })
  afterEach(() => {
    vi.clearAllMocks()
    document.body.innerHTML = ''
  })

  it('lists the task commits with a short hash and message', async () => {
    const { host } = await render(section())
    const list = host.querySelector<HTMLElement>('[aria-label="Commits"]')
    expect(list).not.toBeNull()
    expect(list?.textContent).toContain('Tune the conveyor motor')
    expect(list?.textContent).toContain(short)
    expect(list?.textContent).not.toContain(sha)
  })

  it('states the message is unavailable when the worktree history does not include the commit', async () => {
    vi.mocked(api.getVersionControlWorktreeLog).mockResolvedValue({ repoPath: 'C:/repo', commits: [] })
    const { host } = await render(section())
    expect(host.textContent).toContain(short)
    expect(host.textContent).toContain('Message unavailable')
  })

  it('expands a commit to its author, timestamp, source objects and unresolved-file count', async () => {
    const { host } = await render(section())
    expect(host.querySelector('[data-testid="commit-unresolved-files"]')).toBeNull()
    await expand(host)
    expect(host.textContent).toContain('Ada Lovelace')
    expect(host.textContent).toMatch(/2026/)
    expect(host.textContent).toContain('Function block')
    expect(host.textContent).toContain('Main')
    expect(host.querySelector('[data-testid="commit-unresolved-files"]')?.textContent).toContain('1 changed file')
    expect(vi.mocked(api.getGraphEntityDetail)).toHaveBeenCalledWith('wb1', 'git_commit', sha)
  })

  it('states plainly when a commit resolved no source object instead of rendering an empty list', async () => {
    vi.mocked(api.getGraphEntityDetail).mockResolvedValue(commitEntity([], ['devices/plc/source/blocks/Removed.xml']))
    const { host } = await render(section())
    await expand(host)
    expect(host.querySelector('[data-testid="commit-no-source-objects"]')?.textContent)
      .toContain('No source objects')
    expect(host.querySelector('[data-testid="commit-unresolved-files"]')?.textContent).toContain('1 changed file')
  })

  it('navigates to a source object inside an expanded commit through the sourceObject path', async () => {
    const navigate = vi.fn()
    const { host } = await render(section({ onNavigate: navigate }))
    await expand(host)
    await act(async () => host.querySelector<HTMLButtonElement>('[aria-label="Open source object Main from commit abcdef1"]')?.click())
    expect(navigate).toHaveBeenCalledWith('sourceObject', 'dev-1:block-main')
  })

  it('never renders a commit as a clickable control with no destination', async () => {
    const navigate = vi.fn()
    const { host } = await render(section({ onNavigate: navigate }))
    // The commit is a disclosure: there is no `Open Commits <id>` control, and expanding navigates nowhere.
    expect(host.querySelector('[aria-label="Open Commits abcdef1234567890abcdef1234567890abcdef12"]')).toBeNull()
    await expand(host)
    expect(navigate).not.toHaveBeenCalled()
    expect(host.querySelector<HTMLButtonElement>(`[aria-label="Hide details for commit ${short}"]`)).not.toBeNull()
  })

  it('does not offer a source-object link when the page has no navigation handler', async () => {
    const { host } = await render(section())
    await expand(host)
    expect(host.querySelector('[aria-label="Open source object Main from commit abcdef1"]')).toBeNull()
    expect(host.textContent).toContain('Main')
  })

  it('removes only a manual commit link', async () => {
    const remove = vi.fn()
    const { host } = await render(section({ commits: [{ id: sha, edgeId: 'edge-commit', provenance: 'manual', isPrimary: false }], onRemove: remove }))
    const button = host.querySelector<HTMLButtonElement>(`[aria-label="Remove Commits ${sha}"]`)
    expect(button).not.toBeNull()
    await act(async () => button?.click())
    expect(remove).toHaveBeenCalledWith('commit', expect.objectContaining({ id: sha }))
  })

  it('names a failed evidence read and retries it', async () => {
    vi.mocked(api.getGraphEntityDetail).mockRejectedValueOnce(new Error('graph unavailable'))
    const { host } = await render(section())
    await expand(host)
    expect(host.textContent).toContain('graph unavailable')
    vi.mocked(api.getGraphEntityDetail).mockResolvedValue(commitEntity([sourceEdge('dev-1:block-main')], []))
    await act(async () => Array.from(host.querySelectorAll('button')).find(button => button.textContent === 'Retry')?.click())
    await act(async () => {})
    expect(host.textContent).toContain('Function block')
  })
})
