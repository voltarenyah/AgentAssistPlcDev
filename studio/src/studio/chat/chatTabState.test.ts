import { describe, expect, it } from 'vitest'
import type { ChatSessionData } from '@/api/client'
import {
  appendAssistantDelta,
  appendLocalUserMessage,
  appendProgressMessage,
  closeTab,
  emptyChatTabs,
  openTab,
  renameTab,
} from './chatTabState'

const session = (sessionId: string, title = `Session ${sessionId}`): ChatSessionData => ({
  header: {
    sessionId,
    title,
    projectName: 'PLC_1',
    createdAt: '2026-07-30T00:00:00Z',
    updatedAt: '2026-07-30T00:00:00Z',
  },
  messages: [{ role: 'user', content: `hello ${sessionId}`, toolCallId: null, timestamp: '2026-07-30T00:00:00Z' }],
  roundUsages: [],
})

describe('chat tab state', () => {
  it('opens and focuses a loaded session tab', () => {
    const state = openTab(emptyChatTabs(), session('s1', 'Startup checks'))

    expect(state.activeId).toBe('s1')
    expect(state.tabs).toHaveLength(1)
    expect(state.tabs[0]?.title).toBe('Startup checks')
    expect(state.tabs[0]?.messages[0]?.content).toBe('hello s1')
  })

  it('focuses an existing tab without duplicating it', () => {
    const first = openTab(emptyChatTabs(), session('s1'))
    const next = openTab(first, session('s1', 'Updated title'))

    expect(next.tabs).toHaveLength(1)
    expect(next.activeId).toBe('s1')
    expect(next.tabs[0]?.title).toBe('Updated title')
  })

  it('renames an open tab', () => {
    const state = renameTab(openTab(emptyChatTabs(), session('s1')), 's1', 'Valve diagnosis')

    expect(state.tabs[0]?.title).toBe('Valve diagnosis')
  })

  it('appends a local user message before the saved session reloads', () => {
    const state = appendLocalUserMessage(openTab(emptyChatTabs(), session('s1')), 's1', 'first question')

    expect(state.tabs[0]?.messages.at(-1)?.role).toBe('user')
    expect(state.tabs[0]?.messages.at(-1)?.content).toBe('first question')
  })

  it('appends progress and streamed assistant content to the active tab', () => {
    let state = openTab(emptyChatTabs(), session('s1'))

    state = appendProgressMessage(state, 's1', '-> get_block({"blockName":"FB"})')
    expect(state.tabs[0]?.messages.at(-1)?.role).toBe('tool')
    expect(state.tabs[0]?.messages.at(-1)?.content).toContain('get_block')

    state = appendAssistantDelta(state, 's1', 'This FB')
    state = appendAssistantDelta(state, 's1', ' simulates a cylinder.')

    expect(state.tabs[0]?.messages.at(-1)?.role).toBe('assistant')
    expect(state.tabs[0]?.messages.at(-1)?.content).toBe('This FB simulates a cylinder.')
  })

  it('falls back to the most recently used remaining tab when closing active tab', () => {
    const state = openTab(openTab(emptyChatTabs(), session('s1')), session('s2'))
    const closed = closeTab(state, 's2')

    expect(closed.activeId).toBe('s1')
    expect(closed.tabs.map(tab => tab.sessionId)).toEqual(['s1'])
  })
})
