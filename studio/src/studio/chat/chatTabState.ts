import type { ChatMessage, ChatSessionData, ChatToolStats, ChatUsage } from '@/api/client'
import { lastUsageOf } from './usageDisplay'

export type ChatTab = {
  sessionId: string
  title: string
  messages: ChatMessage[]
  /** Unsent composer text, kept while the chat page is temporarily unmounted. */
  draft?: string
  /** Exact context size of the last billed API round (from the backend). */
  usage?: ChatUsage | null
  /** True when the last turn ended at the tool-round cap; offers "continue". */
  hitRoundCap?: boolean
  /** MCP tool outcomes from the last streamed turn. */
  toolCalls?: ChatToolStats | null
}

export type ChatTabsState = {
  tabs: ChatTab[]
  activeId: string | null
  mru: string[]
}

export const emptyChatTabs = (): ChatTabsState => ({
  tabs: [],
  activeId: null,
  mru: [],
})

const timestamp = () => new Date().toISOString()

const titleOf = (session: ChatSessionData) =>
  session.header.title?.trim() || 'New chat'

const touch = (mru: string[], sessionId: string) => [
  sessionId,
  ...mru.filter(id => id !== sessionId),
]

export function openTab(state: ChatTabsState, session: ChatSessionData): ChatTabsState {
  const existing = state.tabs.find(value => value.sessionId === session.header.sessionId)
  const tab: ChatTab = {
    sessionId: session.header.sessionId,
    title: titleOf(session),
    messages: session.messages,
    draft: existing?.draft ?? '',
    usage: lastUsageOf(session.roundUsages),
    // The cap flag is stream-only (not persisted); keep it across session reloads of an open tab.
    hitRoundCap: existing?.hitRoundCap ?? false,
    toolCalls: existing?.toolCalls ?? null,
  }
  const exists = state.tabs.some(value => value.sessionId === tab.sessionId)
  return {
    tabs: exists
      ? state.tabs.map(value => value.sessionId === tab.sessionId ? tab : value)
      : [...state.tabs, tab],
    activeId: tab.sessionId,
    mru: touch(state.mru, tab.sessionId),
  }
}

/** Applies the end-of-turn meta SSE event (exact usage + round-cap flag) to a tab. */
export function setTurnMeta(
  state: ChatTabsState,
  sessionId: string,
  usage: ChatUsage | null,
  hitRoundCap: boolean,
  toolCalls?: ChatToolStats | null,
): ChatTabsState {
  return {
    ...state,
    tabs: state.tabs.map(tab => tab.sessionId === sessionId ? { ...tab, usage, hitRoundCap, toolCalls: toolCalls ?? null } : tab),
  }
}

export function renameTab(state: ChatTabsState, sessionId: string, title: string): ChatTabsState {
  return {
    ...state,
    tabs: state.tabs.map(tab => tab.sessionId === sessionId ? { ...tab, title } : tab),
  }
}

export function setDraft(
  state: ChatTabsState,
  sessionId: string,
  draft: string,
): ChatTabsState {
  return {
    ...state,
    tabs: state.tabs.map(tab => tab.sessionId === sessionId ? { ...tab, draft } : tab),
  }
}

export function appendLocalUserMessage(
  state: ChatTabsState,
  sessionId: string,
  content: string,
): ChatTabsState {
  return {
    ...state,
    tabs: state.tabs.map(tab => tab.sessionId === sessionId
      ? {
          ...tab,
          messages: [
            ...tab.messages,
            { role: 'user', content, toolCallId: null, timestamp: timestamp() },
          ],
          draft: '',
        }
      : tab),
  }
}

export function appendProgressMessage(
  state: ChatTabsState,
  sessionId: string,
  content: string,
): ChatTabsState {
  return {
    ...state,
    tabs: state.tabs.map(tab => tab.sessionId === sessionId
      ? {
          ...tab,
          messages: [
            ...tab.messages,
            { role: 'tool', content, toolCallId: null, timestamp: timestamp() },
          ],
        }
      : tab),
  }
}

export function appendAssistantDelta(
  state: ChatTabsState,
  sessionId: string,
  delta: string,
): ChatTabsState {
  return {
    ...state,
    tabs: state.tabs.map(tab => {
      if (tab.sessionId !== sessionId) return tab
      const last = tab.messages.at(-1)
      if (last?.role === 'assistant') {
        return {
          ...tab,
          messages: [
            ...tab.messages.slice(0, -1),
            { ...last, content: `${last.content ?? ''}${delta}` },
          ],
        }
      }
      return {
        ...tab,
        messages: [
          ...tab.messages,
          { role: 'assistant', content: delta, toolCallId: null, timestamp: timestamp() },
        ],
      }
    }),
  }
}

export function closeTab(state: ChatTabsState, sessionId: string): ChatTabsState {
  const tabs = state.tabs.filter(tab => tab.sessionId !== sessionId)
  const mru = state.mru.filter(id => id !== sessionId && tabs.some(tab => tab.sessionId === id))
  return {
    tabs,
    activeId: state.activeId === sessionId ? mru[0] ?? null : state.activeId,
    mru,
  }
}
