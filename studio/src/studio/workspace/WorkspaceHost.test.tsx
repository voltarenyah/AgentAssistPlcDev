// @vitest-environment happy-dom
import React, { act } from 'react'
import { createRoot } from 'react-dom/client'
import { afterEach, describe, expect, it, vi } from 'vitest'
import type { DeviceOverviewViewProps } from '@/studio/DeviceOverviewView'
import { emptyChatTabs } from '@/studio/chat/chatTabState'
import WorkspaceHost from './WorkspaceHost'
import { WorkspaceService } from './WorkspaceService'

globalThis.IS_REACT_ACT_ENVIRONMENT = true

// happy-dom has no layout engine; swap in the lightweight FlexLayout stand-in.
vi.mock('flexlayout-react', async () => await import('@/test/flexLayoutMock'))

vi.mock('@/api/client', async importOriginal => {
  const actual = await importOriginal<typeof import('@/api/client')>()
  return {
    ...actual,
    // ChatWorkspace loads settings on mount; never resolving keeps it inert.
    getChatSettings: vi.fn(() => new Promise<never>(() => {})),
  }
})

const overview: DeviceOverviewViewProps = {
  deviceName: 'PLC_1',
  deviceId: 'dev1',
  deviceInfo: null,
  deviceMeta: null,
  deviceView: null,
  blocks: [],
  displayedSourceObjectCount: 0,
  deviceSessions: [],
  activeKnowledge: 'current',
  isBrandNewDevice: false,
  matchingTiaSession: null,
  operation: null,
  rebuildArmed: false,
  setRebuildArmed: vi.fn(),
  activeWorktree: null,
  onOpenProjectInTia: vi.fn(),
  onAttachTiaInstance: vi.fn(),
  onStageRefresh: vi.fn(),
  onRebuildProject: vi.fn(),
  onUpdateKnowledge: vi.fn(),
  onMergeIntoMaster: vi.fn(),
  onBootstrapDevice: vi.fn(),
}

const makeViewProps = () => ({
  overview,
  chat: {
    tabs: emptyChatTabs(),
    busy: false,
    confirmation: null,
    onConfirm: vi.fn(),
    onFocus: vi.fn(),
    onSend: vi.fn(),
    onDraftChange: vi.fn(),
    onStop: vi.fn(),
    onContinue: vi.fn(),
    sourceContext: null,
    onClearSourceContext: vi.fn(),
  },
  source: {
    workbenchId: null,
    worktreeId: null,
    deviceId: null,
    deviceView: null,
    onChatWithAgent: vi.fn(),
    onSnapshotReload: vi.fn(),
  },
  knowledge: {
    context: null,
    projectName: 'PLC_1',
    onNodeSelect: vi.fn(),
    onEdgeSelect: vi.fn(),
  },
  git: {
    workbenchId: 'wb1',
    worktreeId: 'wt1',
    onSelectionChange: vi.fn(),
  },
})

const render = async (element: React.ReactNode) => {
  const host = document.createElement('div')
  document.body.appendChild(host)
  const root = createRoot(host)
  await act(async () => root.render(element))
  return { host, root }
}

const tabButton = (host: HTMLElement, label: string) =>
  Array.from(host.querySelectorAll('button')).find(button => button.textContent?.trim() === label)

afterEach(() => {
  document.body.innerHTML = ''
})

describe('WorkspaceHost', () => {
  it('renders the five workspace tabs and the focused overview view', async () => {
    const workspace = new WorkspaceService()
    const { host } = await render(<WorkspaceHost workspace={workspace} {...makeViewProps()} />)

    for (const label of ['Device overview', 'AI chat', 'PLC source', 'Knowledge', 'Version control']) {
      expect(tabButton(host, label), `tab "${label}"`).toBeTruthy()
    }
    expect(host.querySelector('h1')?.textContent).toBe('PLC_1')
  })

  it('routes model tab selection through the service focus state', async () => {
    const workspace = new WorkspaceService()
    const listener = vi.fn()
    workspace.subscribe(listener)
    const { host } = await render(<WorkspaceHost workspace={workspace} {...makeViewProps()} />)

    await act(async () => {
      tabButton(host, 'AI chat')!.dispatchEvent(new MouseEvent('click', { bubbles: true }))
    })

    expect(workspace.getFocusedViewKind()).toBe('chat')
    expect(listener).toHaveBeenCalledWith('chat')
  })

  it('follows semantic service navigation by selecting the matching tab', async () => {
    const workspace = new WorkspaceService()
    const { host } = await render(<WorkspaceHost workspace={workspace} {...makeViewProps()} />)
    expect(host.querySelector('h1')?.textContent).toBe('PLC_1')

    await act(async () => workspace.showSource())

    expect(host.querySelector('h1')).toBeNull()
    expect(tabButton(host, 'PLC source')?.getAttribute('aria-selected')).toBe('true')
    expect(workspace.getFocusedViewKind()).toBe('source')
  })
})
