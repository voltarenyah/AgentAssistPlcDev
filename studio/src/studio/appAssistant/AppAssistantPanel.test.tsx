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
  it('opens a readable conversation with its own composer from the header chat box', async () => {
    const { host } = render(
      <AppAssistantPanel workbenchId="wb1" workbenchName="Demo" runtime={runtime} defaultExpanded={false} />,
    )
    await act(async () => {})
    const headerInput = host.querySelector<HTMLInputElement>('input[aria-label="Workbench Assistant message"]')!
    expect(host.querySelector('[data-assistant-conversation-composer]')).toBeNull()

    act(() => headerInput.focus())
    const composer = host.querySelector<HTMLFormElement>('[data-assistant-conversation-composer]')
    expect(composer).not.toBeNull()
    const conversationInput = composer!.querySelector<HTMLInputElement>('input[aria-label="Conversation message"]')!
    act(() => {
      const setter = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value')!.set!
      setter.call(conversationInput, 'Summarize the current worktree.')
      conversationInput.dispatchEvent(new Event('input', { bubbles: true }))
    })
    await act(async () => composer!.dispatchEvent(new Event('submit', { bubbles: true, cancelable: true })))
    expect(api.chatAppAssistant).toHaveBeenCalledWith('Summarize the current worktree.', expect.any(String))
    expect(host.querySelector('[data-assistant-message-role="assistant"]')).not.toBeNull()
  })

  it('keeps the conversation and session while the header chat is collapsed', async () => {
    vi.mocked(api.bootstrapAppAssistant).mockResolvedValueOnce([
      { kind: 'state', data: { runtimeSnapshot: runtime, sessionId: 'persistent-session' } },
      { kind: 'answer', data: { answer: 'Ready to help.' } },
    ])
    const props = { workbenchId: 'wb1', workbenchName: 'Demo', runtime, defaultExpanded: false }
    const { host, root } = render(<AppAssistantPanel {...props} />)
    await act(async () => {})

    const conversation = host.querySelector<HTMLElement>('[data-app-assistant-panel]')!
    expect(conversation.getAttribute('aria-hidden')).toBe('true')
    const input = host.querySelector<HTMLInputElement>('input[aria-label="Workbench Assistant message"]')!
    act(() => input.focus())
    expect(conversation.getAttribute('aria-hidden')).toBe('false')

    act(() => {
      const setter = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value')!.set!
      setter.call(input, 'What changed?')
      input.dispatchEvent(new Event('input', { bubbles: true }))
    })
    await act(async () => host.querySelector<HTMLButtonElement>('button[aria-label="Send assistant message"]')!.click())
    expect(api.chatAppAssistant).toHaveBeenCalledWith('What changed?', expect.any(String))
    expect(conversation.textContent).toContain('The worktree remains user-selected.')

    act(() => host.querySelector<HTMLButtonElement>('button[aria-label="Collapse Workbench Assistant"]')!.click())
    expect(conversation.getAttribute('aria-hidden')).toBe('true')
    act(() => host.querySelector<HTMLButtonElement>('button[aria-label="Open Workbench Assistant"]')!.click())
    expect(conversation.getAttribute('aria-hidden')).toBe('false')
    expect(conversation.textContent).toContain('What changed?')
    expect(api.bootstrapAppAssistant).toHaveBeenCalledTimes(1)

    act(() => host.querySelector<HTMLButtonElement>('button[aria-label="Collapse Workbench Assistant"]')!.click())
    expect(conversation.getAttribute('aria-hidden')).toBe('true')
    act(() => root.render(<AppAssistantPanel {...props} confirmation={{ id: 'c1', toolName: 'vc_restore', arguments: '{}', requester: 'persistent-session' }} />))
    expect(conversation.getAttribute('aria-hidden')).toBe('false')
    expect(conversation.querySelector('[data-app-assistant-confirmation="c1"]')).not.toBeNull()
    act(() => host.querySelector<HTMLButtonElement>('button[aria-label="Collapse Workbench Assistant"]')!.click())
    expect(conversation.getAttribute('aria-hidden')).toBe('true')
    expect(host.querySelector('[role="status"]')?.textContent).toBe('Approval needed')
    act(() => host.querySelector<HTMLButtonElement>('button[aria-label="Review Workbench Assistant approval"]')!.click())
    expect(conversation.getAttribute('aria-hidden')).toBe('false')
  })

  it('offers project selection and accepts a question before any selection', async () => {
    const onSelectWorkbench = vi.fn()
    vi.mocked(api.bootstrapAppAssistant).mockResolvedValueOnce([
      { kind: 'state', data: { runtimeSnapshot: null, sessionId: 'global-session' } },
      { kind: 'answer', data: { answer: 'Which project would you like to use?' } },
    ])
    const { host } = render(
      <AppAssistantPanel workbenchId={null} workbenchName="All projects" runtime={null}
        workbenches={[{ workbenchId: 'wb1', name: 'Demo' }]}
        onSelectWorkbench={onSelectWorkbench} />,
    )
    await act(async () => {})

    expect(host.textContent).toContain('Which project would you like to use?')
    expect(host.textContent).toContain('Demo')
    const select = [...host.querySelectorAll<HTMLButtonElement>('button')]
      .find(button => button.textContent === 'Select project')!
    await act(async () => select.click())
    expect(onSelectWorkbench).toHaveBeenCalledWith('wb1')

    const input = host.querySelector<HTMLInputElement>('input[aria-label="Workbench Assistant message"]')!
    act(() => {
      const setter = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value')!.set!
      setter.call(input, 'Help me choose.')
      input.dispatchEvent(new Event('input', { bubbles: true }))
    })
    await act(async () => host.querySelector<HTMLButtonElement>('button[aria-label="Send assistant message"]')!.click())
    expect(api.chatAppAssistant).toHaveBeenCalledWith('Help me choose.', expect.any(String))
  })

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

    expect(api.bootstrapAppAssistant).toHaveBeenCalledWith(expect.any(String))
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

    expect(api.chatAppAssistant).toHaveBeenCalledWith('What should I do next?', expect.any(String))
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
    expect(host.textContent).toContain('Which worktree should be used as the base?')
    expect(host.querySelector('[data-assistant-option="master"]')).not.toBeNull()
    expect(host.textContent).toContain('branch master')
    await act(async () => host.querySelector<HTMLButtonElement>('[data-assistant-option="master"]')!.click())
    expect(api.chatAppAssistant).toHaveBeenLastCalledWith(
      'For "Which worktree should be used as the base?", I choose "master" (value: master). Continue the requested work.',
      expect.any(String),
    )
  })

  it('reports a completed managed creation to refresh the shell', async () => {
    const onManagedChange = vi.fn(async () => {})
    vi.mocked(api.chatAppAssistant).mockResolvedValueOnce([
      { kind: 'state', data: { runtimeSnapshot: runtime, change: { kind: 'task', workbenchId: 'wb1', worktreeId: 'wt1', taskId: 't1' } } },
      { kind: 'answer', data: { answer: 'Created the task.' } },
    ])
    const { host } = render(<AppAssistantPanel workbenchId="wb1" workbenchName="Demo" runtime={runtime} onManagedChange={onManagedChange} />)
    await act(async () => {})
    const input = host.querySelector<HTMLInputElement>('input[aria-label="Workbench Assistant message"]')!
    act(() => {
      const setter = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value')!.set!
      setter.call(input, 'Create the task.')
      input.dispatchEvent(new Event('input', { bubbles: true }))
    })
    await act(async () => host.querySelector<HTMLButtonElement>('button[aria-label="Send assistant message"]')!.click())
    expect(onManagedChange).toHaveBeenCalledWith({ kind: 'task', workbenchId: 'wb1', worktreeId: 'wt1', taskId: 't1', deviceId: null })
    expect(host.textContent).toContain('Created the task.')
  })

  it('normalizes the runtime context envelope returned by the assistant', async () => {
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

  it('shows the pending destructive-tool confirmation and resolves it through the shell', async () => {
    const onConfirm = vi.fn()
    const { host } = render(
      <AppAssistantPanel
        workbenchId="wb1"
        workbenchName="Demo"
        runtime={runtime}
        confirmation={{ id: 'c1', toolName: 'vc_restore', arguments: '{"path":"Blocks/Main.xml"}', requester: 's1' }}
        onConfirm={onConfirm}
      />,
    )
    await act(async () => {})

    // A destructive call suspends the assistant turn, so the approval cannot ride the turn's
    // response body. The shell reads it from the shared server log and hands it down already
    // filtered to this panel's session.
    expect(host.querySelector('[data-app-assistant-confirmation="c1"]')).not.toBeNull()
    expect(host.textContent).toContain('vc_restore')
    expect(host.textContent).toContain('Blocks/Main.xml')

    const buttons = [...host.querySelectorAll<HTMLButtonElement>('[data-app-assistant-confirmation="c1"] button')]
    await act(async () => buttons[0]!.click())
    expect(onConfirm).toHaveBeenCalledWith('allowOnce')
    await act(async () => buttons[1]!.click())
    expect(onConfirm).toHaveBeenLastCalledWith('deny')
  })

  it('allows a pending confirmation while the assistant turn is waiting for it', async () => {
    let resolveTurn!: (events: api.AppAssistantEvent[]) => void
    vi.mocked(api.chatAppAssistant).mockReturnValueOnce(new Promise(resolve => { resolveTurn = resolve }))
    const onConfirm = vi.fn()
    const { host, root } = render(
      <AppAssistantPanel workbenchId="wb1" workbenchName="Demo" runtime={runtime} onConfirm={onConfirm} />,
    )
    await act(async () => {})

    const input = host.querySelector<HTMLInputElement>('input[aria-label="Workbench Assistant message"]')!
    act(() => {
      const setter = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value')!.set!
      setter.call(input, 'Restore the previous version.')
      input.dispatchEvent(new Event('input', { bubbles: true }))
    })
    await act(async () => host.querySelector<HTMLButtonElement>('button[aria-label="Send assistant message"]')!.click())

    await act(async () => root.render(
      <AppAssistantPanel
        workbenchId="wb1"
        workbenchName="Demo"
        runtime={runtime}
        confirmation={{ id: 'c4', toolName: 'vc_restore', arguments: '{}', requester: 's1' }}
        onConfirm={onConfirm}
      />,
    ))
    const approve = host.querySelector<HTMLButtonElement>('[data-app-assistant-confirmation="c4"] button')!
    expect(approve.disabled).toBe(false)
    await act(async () => approve.click())
    expect(onConfirm).toHaveBeenCalledWith('allowOnce')

    resolveTurn([{ kind: 'answer', data: { answer: 'Restored.' } }])
    await act(async () => {})
  })

  it('shows progress while a user turn is running', async () => {
    let resolveTurn!: (events: api.AppAssistantEvent[]) => void
    vi.mocked(api.chatAppAssistant).mockReturnValueOnce(new Promise(resolve => { resolveTurn = resolve }))
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

    expect(host.querySelector('[data-assistant-progress]')?.textContent).toContain('Assistant is working')

    resolveTurn([{ kind: 'answer', data: { answer: 'Done.' } }])
    await act(async () => {})
    expect(host.textContent).toContain('Done.')
  })

  it('reports its own session id so its confirmations can be told apart', async () => {
    const onSessionId = vi.fn()
    vi.mocked(api.bootstrapAppAssistant).mockResolvedValueOnce([
      { kind: 'state', data: { runtimeSnapshot: runtime, sessionId: 'session-9' } },
      { kind: 'answer', data: { answer: 'Ready.' } },
    ])
    render(
      <AppAssistantPanel workbenchId="wb1" workbenchName="Demo" runtime={runtime} onSessionId={onSessionId} />,
    )

    await vi.waitFor(() => expect(onSessionId).toHaveBeenCalledWith('session-9'))
  })

  it('does not auto-refresh while a destructive-tool approval is pending', async () => {
    const { host } = render(
      <AppAssistantPanel
        workbenchId="wb1"
        workbenchName="Demo"
        runtime={runtime}
        confirmation={{ id: 'c3', toolName: 'vc_add_worktree', arguments: '{}', requester: 's1' }}
      />,
    )
    await act(async () => {})
    vi.mocked(api.chatAppAssistant).mockClear()

    act(() => runtimeHarness.listener?.({
      ...runtime,
      workbenchRevision: 4,
      worktrees: [...runtime.worktrees, { worktreeId: 'wt2', name: 'feature', branch: 'feature', todoCount: 0, gitStatus: 'clean' }],
    }))
    await act(async () => {})

    expect(api.chatAppAssistant).not.toHaveBeenCalled()
    expect(host.querySelector('[data-app-assistant-confirmation="c3"]')).not.toBeNull()
  })

  it('shows the full argument payload of a workbench creation proposal', async () => {
    const { host } = render(
      <AppAssistantPanel
        workbenchId="wb1"
        workbenchName="Demo"
        runtime={runtime}
        confirmation={{
          id: 'c2',
          toolName: 'assistant_create_workbench',
          arguments: '{"name":"Assistant Project","engineeringProjectPath":"C:/Projects/Line.ap17"}',
          requester: 's1',
        }}
      />,
    )
    await act(async () => {})

    expect(host.textContent).toContain('Create workbench')
    expect(host.textContent).toContain('C:/Projects/Line.ap17')
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

    expect(api.bootstrapAppAssistant).toHaveBeenCalledTimes(2)
    expect(host.textContent).toContain('feature')
    // The refresh must hand the panel back: its own busy/autoRefreshPending updates are its
    // dependencies, so a per-run cancellation flag would make React cancel the request it just
    // started and leave the message box disabled forever.
    await vi.waitFor(() => expect(
      host.querySelector<HTMLInputElement>('input[aria-label="Workbench Assistant message"]')?.disabled,
    ).toBe(false))
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

    expect(api.bootstrapAppAssistant).toHaveBeenCalledTimes(2)
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
