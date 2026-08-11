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
    submitAppAssistantFeedback: vi.fn(async () => {}),
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

  it('records a categorized outcome for the latest assistant run', async () => {
    vi.mocked(api.bootstrapAppAssistant).mockResolvedValueOnce([
      { kind: 'state', data: { runtimeSnapshot: runtime, runMetadata: { runId: 'run-1' } } },
      { kind: 'answer', data: { answer: 'Review the current todo list.' } },
    ])
    const { host } = render(
      <AppAssistantPanel workbenchId="wb1" workbenchName="Demo" runtime={runtime} />,
    )
    await act(async () => {})

    const button = host.querySelector<HTMLButtonElement>('[data-assistant-feedback="successful_completion"]')
    expect(button).not.toBeNull()
    await act(async () => button!.click())

    expect(api.submitAppAssistantFeedback).toHaveBeenCalledWith('successful_completion', 'run-1')
  })
})
