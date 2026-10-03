// @vitest-environment happy-dom
import React, { act } from 'react'
import { createRoot } from 'react-dom/client'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import * as api from '@/api/client'
import type { DeviceSnapshot, SourceObjectInfo, TaskSourceStage, WorktreeSourceStage } from '@/api/client'
import TaskSourceObjectsSection from './TaskSourceObjectsSection'

const toastMock = vi.hoisted(() => ({ error: vi.fn() }))

vi.mock('sonner', () => ({ toast: toastMock }))
vi.mock('@/api/client', () => ({
  listTaskSourceStages: vi.fn(),
  listWorktreeSourceStages: vi.fn(),
  getDeviceInfo: vi.fn(),
  stageTaskSourceObject: vi.fn(),
  releaseTaskSourceObject: vi.fn(),
}))
vi.mock('@/components/ui/toast', () => ({ showErrorToast: toastMock.error }))

globalThis.IS_REACT_ACT_ENVIRONMENT = true

const sourceObject = (id: string, name: string, category: string, relativePath: string): SourceObjectInfo => ({
  id,
  name,
  number: null,
  category,
  programmingLanguage: null,
  groupPath: null,
  relativePath,
  contentHash: null,
  isKnowHowProtected: null,
  modifiedDate: null,
  status: null,
})

const deviceSnapshot = (sourceObjects: SourceObjectInfo[]) => ({ sourceObjects } as unknown as DeviceSnapshot)

const stage = (sourceObjectId: string, baselineEvidenceJson: string | null = '{ "id": "main" }'): TaskSourceStage => ({
  taskId: 'task-1',
  sourceObjectId,
  deviceId: 'device-1',
  baselineEvidenceJson,
  stagedUtc: '2026-09-30T00:00:00Z',
})

const render = async (element: React.ReactNode) => {
  const host = document.createElement('div')
  document.body.appendChild(host)
  const root = createRoot(host)
  await act(async () => root.render(element))
  await act(async () => {})
  return { host, root }
}

const setInput = async (input: HTMLInputElement, value: string) => {
  const setter = Object.getOwnPropertyDescriptor(window.HTMLInputElement.prototype, 'value')!.set!
  await act(async () => {
    setter.call(input, value)
    input.dispatchEvent(new Event('input', { bubbles: true }))
  })
}

/** cmdk selects an item from the input's keyboard navigation (the established TagPicker pattern).
 * The two keys need separate flushes: Enter selects whatever the arrow key marked active. */
const selectFirstMatch = async () => {
  const input = document.querySelector<HTMLInputElement>('[aria-label="Search source objects"]')!
  await act(async () => {
    input.dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowDown', bubbles: true }))
  })
  await act(async () => {
    input.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true }))
  })
}

const section = (props: Partial<React.ComponentProps<typeof TaskSourceObjectsSection>> = {}) => (
  <TaskSourceObjectsSection
    workbenchId="wb1"
    worktreeId="wt1"
    taskId="task-1"
    deviceId="device-1"
    deviceName="Line 4 conveyor PLC"
    {...props}
  />
)

describe('TaskSourceObjectsSection', () => {
  beforeEach(() => {
    vi.mocked(api.listTaskSourceStages).mockReset()
    vi.mocked(api.listWorktreeSourceStages).mockReset()
    vi.mocked(api.getDeviceInfo).mockReset()
    vi.mocked(api.stageTaskSourceObject).mockReset()
    vi.mocked(api.releaseTaskSourceObject).mockReset()
    toastMock.error.mockReset()
    vi.mocked(api.listTaskSourceStages).mockResolvedValue([])
    vi.mocked(api.listWorktreeSourceStages).mockResolvedValue([])
    vi.mocked(api.stageTaskSourceObject).mockResolvedValue(stage('device-1:main'))
    vi.mocked(api.releaseTaskSourceObject).mockResolvedValue(undefined)
    vi.mocked(api.getDeviceInfo).mockResolvedValue(deviceSnapshot([
      sourceObject('main', 'Main', 'OB', 'Blocks/Main.xml'),
      sourceObject('plant', 'Plant', 'Tags', 'Tags/Plant.xml'),
    ]))
  })

  afterEach(() => {
    document.body.innerHTML = ''
  })

  it('lists the active stages with name, category and owning device', async () => {
    vi.mocked(api.listTaskSourceStages).mockResolvedValue([stage('device-1:main')])

    const { host } = await render(section())

    const region = host.querySelector<HTMLElement>('[aria-label="Source objects"]')!
    expect(region.textContent).toContain('Main')
    expect(region.textContent).toContain('OB')
    expect(region.textContent).toContain('Line 4 conveyor PLC')
    expect(region.querySelector('[aria-label="Add source object"]')).not.toBeNull()
  })

  it('explains a stage that has no committed Git content instead of implying a baseline', async () => {
    vi.mocked(api.listTaskSourceStages).mockResolvedValue([stage('device-1:main', null)])

    const { host } = await render(section())

    expect(host.querySelector('[data-testid="stage-baseline-missing"]')?.textContent)
      .toContain('No committed Git content yet')
  })

  it('offers the add action in the empty state and filters the dialog by query and type', async () => {
    const { host } = await render(section())

    expect(host.textContent).toContain('No source objects staged yet.')
    await act(async () => host.querySelector<HTMLButtonElement>('[aria-label="Add source object"]')?.click())

    expect(document.body.textContent).toContain('Plant')
    await setInput(document.querySelector<HTMLInputElement>('[aria-label="Search source objects"]')!, 'plant')
    expect(document.body.textContent).toContain('Plant')
    expect(document.body.textContent).not.toContain('Main')

    await act(async () => document.querySelector<HTMLButtonElement>('[aria-label="Filter by UDT"]')?.click())
    expect(document.body.textContent).toContain('No source objects match.')

    await act(async () => document.querySelector<HTMLButtonElement>('[aria-label="Filter by Tag table"]')?.click())
    await selectFirstMatch()
    expect(api.stageTaskSourceObject).toHaveBeenCalledWith('wb1', 'wt1', 'task-1', 'device-1:plant')
  })

  it('releases the stage when Remove is used', async () => {
    vi.mocked(api.listTaskSourceStages).mockResolvedValue([stage('device-1:main')])
    const onChanged = vi.fn()

    const { host } = await render(section({ onChanged }))
    await act(async () => host.querySelector<HTMLButtonElement>('[aria-label="Remove source object Main"]')?.click())

    expect(api.releaseTaskSourceObject).toHaveBeenCalledWith('wb1', 'wt1', 'task-1', 'device-1:main')
    expect(onChanged).toHaveBeenCalled()
  })

  it('shows the owning task and requires an explicit take-over', async () => {
    const owner: WorktreeSourceStage = {
      taskId: 'task-2',
      taskTitle: 'Motor rework',
      sourceObjectId: 'device-1:main',
      deviceId: 'device-1',
      baselineEvidenceJson: null,
      stagedUtc: '2026-09-30T00:00:00Z',
    }
    vi.mocked(api.listWorktreeSourceStages).mockResolvedValue([owner])

    const { host } = await render(section())
    await act(async () => host.querySelector<HTMLButtonElement>('[aria-label="Add source object"]')?.click())
    await setInput(document.querySelector<HTMLInputElement>('[aria-label="Search source objects"]')!, 'main')

    expect(document.body.textContent).toContain('Owned by Motor rework')
    // Selecting the row is not enough: an object owned by another task never stages implicitly.
    await selectFirstMatch()
    expect(api.stageTaskSourceObject).not.toHaveBeenCalled()

    await act(async () => document.querySelector<HTMLButtonElement>('[aria-label="Take over OB Main from Motor rework"]')?.click())
    expect(api.releaseTaskSourceObject).toHaveBeenCalledWith('wb1', 'wt1', 'task-2', 'device-1:main')
    expect(api.stageTaskSourceObject).toHaveBeenCalledWith('wb1', 'wt1', 'task-1', 'device-1:main')
  })

  it('reports a failed stage load and retries', async () => {
    vi.mocked(api.listTaskSourceStages).mockRejectedValueOnce(new Error('graph unavailable'))

    const { host } = await render(section())

    expect(host.textContent).toContain('graph unavailable')
    await act(async () => host.querySelector<HTMLButtonElement>('button')?.click())
    expect(api.listTaskSourceStages).toHaveBeenCalledTimes(2)
  })
})
