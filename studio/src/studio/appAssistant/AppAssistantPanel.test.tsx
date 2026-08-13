// @vitest-environment happy-dom
import React, { act } from 'react'
import { createRoot } from 'react-dom/client'
import { afterEach, describe, expect, it, vi } from 'vitest'
import AppAssistantPanel from './AppAssistantPanel'
import * as api from '@/api/client'

const runtimeHarness = vi.hoisted(() => ({
  listener: null as ((snapshot: api.AppAssistantRuntimeSnapshot) => void) | null,
}))

vi.mock('@/api/client', async importOriginal => {
  const actual = await importOriginal<typeof import('@/api/client')>()
  return {
    ...actual,
    bootstrapAppAssistant: vi.fn(async () => [{ kind: 'answer', data: { answer: 'Start by reviewing the open worktree todos.' } }]),
    chatAppAssistant: vi.fn(async () => [{ kind: 'answer', data: { answer: 'The worktree remains user-selected.' } }]),
    subscribeAppAssistantRuntime: vi.fn((_workbenchId: string, listener: (snapshot: api.AppAssistantRuntimeSnapshot) => void) => {
      runtimeHarness.listener = listener
      return () => { runtimeHarness.listener = null }
    }),
  }
})

const runtime: api.AppAssistantRuntimeSnapshot = {
  schemaVersion: 1,
  workbenchId: 'wb1',
  workbenchRevision: 3,
  focus: { worktreeId: 'wt1', deviceId: null },
  worktrees: [{ worktreeId: 'wt1', name: 'master', branch: 'master', todoCount: 2, gitStatus: 'clean' }],
  availableActions: [],
  operation: { status: 'idle', operationId: null, kind: null, message: null },
  observedAt: '2026-08-09T00:00:00Z',
}

const render = (element: React.ReactNode) => {
  const host = document.createElement('div')
  document.body.appendChild(host)
  const root = createRoot(host)
  act(() => root.render(element))
  return { host, root }
}

afterEach(() => {
  document.body.innerHTML = ''
  runtimeHarness.listener = null
  vi.clearAllMocks()
})

describe('AppAssistantPanel', () => {
  it('keeps the first command disabled until orientation has completed', async () => {
    let resolveBootstrap: ((events: api.AppAssistantEvent[]) => void) | undefined
    vi.mocked(api.bootstrapAppAssistant).mockReturnValueOnce(new Promise(resolve => {
      resolveBootstrap = resolve
    }))
    const { host } = render(
      <AppAssistantPanel workbenchId="wb1" workbenchName="Demo" runtime={runtime} />,
    )

    expect(host.querySelector<HTMLInputElement>('input[aria-label="Workbench Assistant message"]')?.disabled).toBe(true)

    await act(async () => resolveBootstrap?.([{ kind: 'answer', data: { answer: 'Ready.' } }]))
    expect(host.querySelector<HTMLInputElement>('input[aria-label="Workbench Assistant message"]')?.disabled).toBe(false)
  })

  it('shows orientation and waits for an explicit user command', async () => {
    vi.mocked(api.bootstrapAppAssistant).mockResolvedValueOnce([
      { kind: 'answer', data: { answer: 'Likely intention: review the worktree. Would you like me to read the focused todo list?' } },
    ])
    const { host } = render(
      <AppAssistantPanel workbenchId="wb1" workbenchName="Demo" runtime={runtime} />,
    )
    await act(async () => {})

    expect(api.bootstrapAppAssistant).toHaveBeenCalledWith()
    expect(api.chatAppAssistant).not.toHaveBeenCalled()
    expect(host.textContent).toContain('Would you like me to read the focused todo list?')
  })

  it('shows initial orientation and keeps the selected worktree under user control', async () => {
    const { host } = render(
      <AppAssistantPanel workbenchId="wb1" workbenchName="Demo" runtime={runtime} />,
    )
    await act(async () => {})

    expect(host.textContent).toContain('Workbench Assistant')
    expect(host.textContent).toContain('Start by reviewing the open worktree todos.')
    expect(host.textContent).toContain('master')
    expect(host.querySelector('[data-assistant-select-worktree]')).toBeNull()
  })

  it('sends a user question through the separate assistant endpoint', async () => {
    const { host } = render(
      <AppAssistantPanel workbenchId="wb1" workbenchName="Demo" runtime={runtime} />,
    )
    await act(async () => {})
    const input = host.querySelector<HTMLInputElement>('input[aria-label="Workbench Assistant message"]')!
    act(() => {
      const setter = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value')!.set!
      setter.call(input, 'What should I do next?')
      input.dispatchEvent(new Event('input', { bubbles: true }))
    })
    await act(async () => {})
    const button = host.querySelector<HTMLButtonElement>('button[aria-label="Send assistant message"]')!
    await act(async () => button.click())

    expect(api.chatAppAssistant).toHaveBeenCalledWith('What should I do next?')
    expect(host.textContent).toContain('The worktree remains user-selected.')
  })

  it('renders concrete baseline choices returned by the assistant', async () => {
    vi.mocked(api.chatAppAssistant).mockResolvedValueOnce([
      {
        kind: 'state',
        data: {
          runtimeSnapshot: runtime,
          decision: {
            kind: 'clarification',
            question: 'Which worktree should be used as the base?',
            options: [{ value: 'master', label: 'master', description: 'branch master' }],
          },
        },
      },
      { kind: 'answer', data: { answer: 'Choose a base worktree.' } },
    ])
    const { host } = render(
      <AppAssistantPanel workbenchId="wb1" workbenchName="Demo" runtime={runtime} />,
    )
    await act(async () => {})
    const input = host.querySelector<HTMLInputElement>('input[aria-label="Workbench Assistant message"]')!
    act(() => {
      const setter = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value')!.set!
      setter.call(input, 'Create a new worktree named test.')
      input.dispatchEvent(new Event('input', { bubbles: true }))
    })
    await act(async () => host.querySelector<HTMLButtonElement>('button[aria-label="Send assistant message"]')?.click())

    expect(host.textContent).toContain('Choose a base worktree.')
    expect(host.querySelector('[data-assistant-option="master"]')).not.toBeNull()
    expect(host.textContent).toContain('branch master')
  })

  it('normalizes the runtime context envelope returned by the sidecar', async () => {
    vi.mocked(api.bootstrapAppAssistant).mockResolvedValueOnce([
      {
        kind: 'state',
        data: {
          runtimeSnapshot: {
            workbenchId: 'wb1',
            name: 'Demo',
            runtime,
            availableActions: [],
            observedAt: runtime.observedAt,
          },
        },
      },
      { kind: 'answer', data: { answer: 'Ready.' } },
    ])

    const { host } = render(
      <AppAssistantPanel workbenchId="wb1" workbenchName="Demo" runtime={null} onSelectWorktree={vi.fn()} />,
    )
    await vi.waitFor(() => expect(host.textContent).toContain('master'))

    expect(host.querySelector('[data-app-assistant-panel]')).not.toBeNull()
  })

  it('keeps a mutation proposal visible until the user approves or rejects it', async () => {
    vi.mocked(api.chatAppAssistant).mockResolvedValueOnce([
      {
        kind: 'interrupt',
        data: { kind: 'create_worktree', name: 'langgraph-test', branch: 'assistant/langgraph-test' },
      },
      { kind: 'answer', data: { answer: 'Please approve the proposed worktree creation.' } },
    ])
    const { host } = render(
      <AppAssistantPanel workbenchId="wb1" workbenchName="Demo" runtime={runtime} />,
    )
    await act(async () => {})

    const input = host.querySelector<HTMLInputElement>('input[aria-label="Workbench Assistant message"]')!
    act(() => {
      const setter = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value')!.set!
      setter.call(input, 'Create a new worktree named langgraph-test.')
      input.dispatchEvent(new Event('input', { bubbles: true }))
    })
    await act(async () => host.querySelector<HTMLButtonElement>('button[aria-label="Send assistant message"]')?.click())

    expect(host.textContent).toContain('Approve worktree creation?')
    expect(host.querySelector<HTMLButtonElement>('.primary-button')?.textContent).toContain('Approve')
  })

  it('shows visible progress while an approved worktree is being created', async () => {
    vi.mocked(api.chatAppAssistant).mockResolvedValueOnce([
      {
        kind: 'interrupt',
        data: { kind: 'create_worktree', name: 'slow-test', branch: 'assistant/slow-test' },
      },
      { kind: 'answer', data: { answer: 'Please approve the proposed worktree creation.' } },
    ])
    const { host } = render(
      <AppAssistantPanel workbenchId="wb1" workbenchName="Demo" runtime={runtime} />,
    )
    await act(async () => {})

    const input = host.querySelector<HTMLInputElement>('input[aria-label="Workbench Assistant message"]')!
    act(() => {
      const setter = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value')!.set!
      setter.call(input, 'Create a new worktree named slow-test.')
      input.dispatchEvent(new Event('input', { bubbles: true }))
    })
    await act(async () => host.querySelector<HTMLButtonElement>('button[aria-label="Send assistant message"]')?.click())

    let resolveApproval!: (events: api.AppAssistantEvent[]) => void
    vi.mocked(api.chatAppAssistant).mockReturnValueOnce(new Promise(resolve => { resolveApproval = resolve }))
    await act(async () => host.querySelector<HTMLButtonElement>('button')?.click())

    expect(host.querySelector('[data-assistant-progress]')?.textContent).toContain('Creating linked worktree')
    resolveApproval([{ kind: 'answer', data: { answer: 'Created.' } }])
    await act(async () => {})
  })

  it('shows visible progress while an approved workbench is being created', async () => {
    vi.mocked(api.chatAppAssistant).mockResolvedValueOnce([
      {
        kind: 'interrupt',
        data: { kind: 'create_workbench', name: 'slow-project', engineeringProjectPath: 'C:\\Projects\\Line.ap17' },
      },
      { kind: 'answer', data: { answer: 'Please approve the proposed workbench creation.' } },
    ])
    const { host } = render(
      <AppAssistantPanel workbenchId="wb1" workbenchName="Demo" runtime={runtime} />,
    )
    await act(async () => {})

    const input = host.querySelector<HTMLInputElement>('input[aria-label="Workbench Assistant message"]')!
    act(() => {
      const setter = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value')!.set!
      setter.call(input, 'Create a new project from C:\\Projects\\Line.ap17.')
      input.dispatchEvent(new Event('input', { bubbles: true }))
    })
    await act(async () => host.querySelector<HTMLButtonElement>('button[aria-label="Send assistant message"]')?.click())

    let resolveApproval!: (events: api.AppAssistantEvent[]) => void
    vi.mocked(api.chatAppAssistant).mockReturnValueOnce(new Promise(resolve => { resolveApproval = resolve }))
    await act(async () => host.querySelector<HTMLButtonElement>('button')?.click())

    expect(host.querySelector('[data-assistant-progress]')?.textContent).toContain('Creating workbench project')
    resolveApproval([{ kind: 'answer', data: { answer: 'Created.' } }])
    await act(async () => {})
  })

  it('does not auto-refresh a paused mutation thread while approval is pending', async () => {
    vi.mocked(api.chatAppAssistant).mockResolvedValueOnce([
      {
        kind: 'interrupt',
        data: { kind: 'create_worktree', name: 'paused-test', branch: 'assistant/paused-test' },
      },
      { kind: 'answer', data: { answer: 'Please approve the proposed worktree creation.' } },
    ])
    const { host } = render(
      <AppAssistantPanel workbenchId="wb1" workbenchName="Demo" runtime={runtime} />,
    )
    await act(async () => {})

    const input = host.querySelector<HTMLInputElement>('input[aria-label="Workbench Assistant message"]')!
    act(() => {
      const setter = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value')!.set!
      setter.call(input, 'Create a new worktree named paused-test.')
      input.dispatchEvent(new Event('input', { bubbles: true }))
    })
    await act(async () => host.querySelector<HTMLButtonElement>('button[aria-label="Send assistant message"]')?.click())
    vi.mocked(api.chatAppAssistant).mockClear()

    act(() => runtimeHarness.listener?.({
      ...runtime,
      workbenchRevision: 4,
      worktrees: [...runtime.worktrees, { worktreeId: 'wt2', name: 'feature', branch: 'feature', todoCount: 0, gitStatus: 'clean' }],
    }))
    await act(async () => {})

    expect(api.chatAppAssistant).not.toHaveBeenCalled()
    expect(host.textContent).toContain('Approve worktree creation?')
  })

  it('shows the TIA source path for a new workbench proposal', async () => {
    vi.mocked(api.chatAppAssistant).mockResolvedValueOnce([
      {
        kind: 'interrupt',
        data: {
          kind: 'create_workbench',
          name: 'Assistant Project',
          engineeringProjectPath: 'C:\\Projects\\Line.ap17',
        },
      },
      { kind: 'answer', data: { answer: 'A workbench creation proposal is ready.' } },
    ])
    const { host } = render(
      <AppAssistantPanel workbenchId="wb1" workbenchName="Demo" runtime={runtime} />,
    )
    await act(async () => {})

    const input = host.querySelector<HTMLInputElement>('input[aria-label="Workbench Assistant message"]')!
    act(() => {
      const setter = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value')!.set!
      setter.call(input, 'Create a new project from C:\\Projects\\Line.ap17.')
      input.dispatchEvent(new Event('input', { bubbles: true }))
    })
    await act(async () => host.querySelector<HTMLButtonElement>('button[aria-label="Send assistant message"]')?.click())

    expect(host.textContent).toContain('Approve workbench creation?')
    expect(host.textContent).toContain('C:\\Projects\\Line.ap17')
  })

  it('automatically re-bootstraps after a consequential runtime change', async () => {
    const { host } = render(
      <AppAssistantPanel workbenchId="wb1" workbenchName="Demo" runtime={runtime} />,
    )
    await act(async () => {})
    expect(api.bootstrapAppAssistant).toHaveBeenCalledTimes(1)

    act(() => runtimeHarness.listener?.({
      ...runtime,
      workbenchRevision: 4,
      worktrees: [{ worktreeId: 'wt2', name: 'feature', branch: 'feature', todoCount: 0, gitStatus: 'dirty' }],
    }))
    await act(async () => {})

    expect(api.chatAppAssistant).toHaveBeenCalledWith('The workbench changed. Re-read the current state and suggest the next useful move.')
    expect(host.textContent).toContain('feature')
  })

  it('refreshes the assistant when the focused worktree changes', async () => {
    const { host } = render(
      <AppAssistantPanel workbenchId="wb1" workbenchName="Demo" runtime={runtime} />,
    )
    await act(async () => {})
    vi.mocked(api.chatAppAssistant).mockClear()

    act(() => runtimeHarness.listener?.({
      ...runtime,
      workbenchRevision: 4,
      focus: { worktreeId: 'wt2', deviceId: null },
    }))
    await act(async () => {})

    expect(api.chatAppAssistant).toHaveBeenCalledWith('The workbench changed. Re-read the current state and suggest the next useful move.')
    expect(host.textContent).toContain('context changed')
  })

  it('offers an explicit selection action for an unselected worktree', async () => {
    const selectWorktree = vi.fn(async () => {})
    const { host } = render(
      <AppAssistantPanel
        workbenchId="wb1"
        workbenchName="Demo"
        runtime={{
          ...runtime,
          worktrees: [...runtime.worktrees, { worktreeId: 'wt2', name: 'feature', branch: 'feature', todoCount: 0, gitStatus: 'clean' }],
        }}
        onSelectWorktree={selectWorktree}
      />,
    )
    await act(async () => {})

    const button = host.querySelector<HTMLButtonElement>('[data-assistant-select-worktree="wt2"]')
    expect(button).not.toBeNull()
    await act(async () => button!.click())

    expect(selectWorktree).toHaveBeenCalledWith('wt2')
  })

  it('does not show an optional feedback card in the normal assistant conversation', async () => {
    vi.mocked(api.bootstrapAppAssistant).mockResolvedValueOnce([
      { kind: 'state', data: { runtimeSnapshot: runtime, runMetadata: { runId: 'run-1' } } },
      { kind: 'answer', data: { answer: 'Review the current todo list.' } },
    ])
    const { host } = render(
      <AppAssistantPanel workbenchId="wb1" workbenchName="Demo" runtime={runtime} />,
    )
    await act(async () => {})

    expect(host.querySelector('[data-assistant-feedback]')).toBeNull()
  })
})
