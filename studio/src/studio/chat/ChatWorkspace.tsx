import { useCallback, useEffect, useRef, useState } from 'react'
import { Ban, Loader2, MessageSquare, Send, Square, Wrench, XCircle } from 'lucide-react'
import ReactMarkdown from 'react-markdown'
import remarkGfm from 'remark-gfm'
import * as api from '@/api/client'
import type { ChatMessage, ChatUsage } from '@/api/client'
import type { ChatTabsState } from './chatTabState'
import { parseProgressContent, progressTitle } from './progressDisplay'
import { contextLabel } from './usageDisplay'

type Props = {
  tabs: ChatTabsState
  busy: boolean
  onFocus: (sessionId: string) => void
  onSend: (sessionId: string, message: string) => void
  onStop: () => void
  onContinue: (sessionId: string) => void
}

const roleLabel = (message: ChatMessage) =>
  message.role === 'assistant' ? 'Assistant'
    : message.role === 'user' ? 'You'
      : message.role === 'tool' ? progressTitle(parseProgressContent(message.content ?? ''))
        : message.role

const messageTone = (message: ChatMessage) =>
  message.role === 'tool'
    ? 'bg-muted/30 text-muted-foreground'
    : 'bg-card'

type SettingsSaveState = 'idle' | 'saving' | 'saved' | 'error'

const MODEL_OPTIONS = [
  { value: 'deepseek-v4-flash', label: 'Flash' },
  { value: 'deepseek-v4-pro', label: 'Pro' },
]

const EFFORT_OPTIONS = ['low', 'medium', 'high']

function ChatComposer({
  sessionId,
  disabled,
  busy,
  settings,
  settingsState,
  usage,
  onSettingsChange,
  onSend,
  onStop,
}: {
  sessionId: string
  disabled: boolean
  busy: boolean
  settings: api.ChatSettings | null
  settingsState: SettingsSaveState
  usage?: ChatUsage | null
  onSettingsChange: (patch: Partial<api.ChatSettings>) => void
  onSend: (sessionId: string, message: string) => void
  onStop: () => void
}) {
  const knownModel = Boolean(settings && MODEL_OPTIONS.some(option => option.value === settings.model))
  const context = contextLabel(usage, settings?.contextWindow)
  return (
    <form
      data-chat-composer={sessionId}
      className="border-t p-3"
      style={{ borderColor: 'var(--border)' }}
      onSubmit={event => {
        event.preventDefault()
        const data = new FormData(event.currentTarget)
        const message = data.get('message')?.toString().trim() ?? ''
        if (!message) return
        onSend(sessionId, message)
        event.currentTarget.reset()
      }}
    >
      <div className="flex gap-2">
        <textarea
          name="message"
          className="field-input min-h-16 flex-1 resize-none py-2"
          disabled={disabled}
          placeholder="Ask about this PLC device..."
        />
        {busy ? (
          <button
            type="button"
            className="secondary-button h-16 px-3 text-red-600 dark:text-red-400"
            onClick={onStop}
            aria-label="Stop generation"
            title="Stop generation"
          >
            <Square className="h-3.5 w-3.5 fill-current" />
          </button>
        ) : (
          <button className="primary-button h-16 px-3" disabled={disabled} aria-label="Send message">
            <Send className="h-3.5 w-3.5" />
          </button>
        )}
      </div>
      <div
        className="mt-2 flex flex-wrap items-center gap-2 text-[9px] text-muted-foreground"
        data-chat-settings
      >
        <select
          aria-label="Model"
          className="field-input h-6 w-auto px-1 py-0 text-[9px]"
          value={settings?.model ?? ''}
          disabled={!settings}
          onChange={event => onSettingsChange({ model: event.target.value })}
        >
          {!settings && <option value="">Loading…</option>}
          {settings && !knownModel && <option value={settings.model}>{settings.model}</option>}
          {MODEL_OPTIONS.map(option => (
            <option key={option.value} value={option.value}>{option.label}</option>
          ))}
        </select>
        <button
          type="button"
          aria-label="Toggle think mode"
          aria-pressed={settings?.thinkingEnabled ?? false}
          disabled={!settings}
          className={`h-6 rounded-md border px-2 ${settings?.thinkingEnabled ? 'bg-accent text-foreground' : ''}`}
          style={{ borderColor: 'var(--border)' }}
          onClick={() => settings && onSettingsChange({ thinkingEnabled: !settings.thinkingEnabled })}
        >
          Think {settings?.thinkingEnabled ? 'on' : 'off'}
        </button>
        {settings?.thinkingEnabled && (
          <select
            aria-label="Think effort"
            className="field-input h-6 w-auto px-1 py-0 text-[9px]"
            value={settings.reasoningEffort}
            onChange={event => onSettingsChange({ reasoningEffort: event.target.value })}
          >
            {!EFFORT_OPTIONS.includes(settings.reasoningEffort) && (
              <option value={settings.reasoningEffort}>{settings.reasoningEffort}</option>
            )}
            {EFFORT_OPTIONS.map(effort => (
              <option key={effort} value={effort}>{effort}</option>
            ))}
          </select>
        )}
        <label className="flex items-center gap-1">
          Temp
          <input
            type="number"
            aria-label="Temperature"
            className="field-input h-6 w-14 px-1 py-0 text-[9px]"
            min={0}
            max={2}
            step={0.1}
            value={settings?.temperature ?? ''}
            disabled={!settings}
            onChange={event => {
              if (event.target.value === '') return
              const value = Number(event.target.value)
              if (Number.isFinite(value)) onSettingsChange({ temperature: Math.min(2, Math.max(0, value)) })
            }}
          />
        </label>
        {context && (
          <span data-chat-context title={context}>{context}</span>
        )}
        <span className="ml-auto" data-chat-settings-state>
          {settingsState === 'saving' ? 'Saving…'
            : settingsState === 'saved' ? 'Saved'
              : settingsState === 'error' ? (settings ? 'Save failed' : 'Settings unavailable')
                : ''}
        </span>
      </div>
    </form>
  )
}

function BusyRow() {
  return (
    <div className="flex items-center gap-2 rounded-lg border bg-muted/30 p-3 text-[10px] text-muted-foreground" style={{ borderColor: 'var(--border)' }}>
      <Loader2 className="h-3.5 w-3.5 animate-spin" />
      Assistant is working...
    </div>
  )
}

function ProgressBody({ content }: { content: string }) {
  const entries = parseProgressContent(content)
  return (
    <div className="space-y-1.5">
      {entries.map((entry, index) => {
        if (entry.kind === 'note') {
          return (
            <div key={index} className="text-[9px] leading-relaxed text-muted-foreground">
              {entry.text}
            </div>
          )
        }
        if (entry.kind === 'tool-call') {
          return (
            <div key={index} className="rounded-md border bg-background/60 p-2" style={{ borderColor: 'var(--border)' }}>
              <div className="flex items-center gap-1.5 text-[9px] font-medium text-foreground">
                <Wrench className="h-3 w-3 text-chart-3" />
                <span className="font-mono">{entry.name}</span>
              </div>
              {entry.args && entry.args !== '{}' && (
                <pre className="mt-1 whitespace-pre-wrap break-all rounded bg-muted/40 p-1.5 font-mono text-[8px] text-muted-foreground">{entry.args}</pre>
              )}
            </div>
          )
        }
        return (
          <div key={index} className="rounded-md border border-red-500/30 bg-red-500/5 p-2">
            <div className="flex items-center gap-1.5 text-[9px] font-medium text-red-600 dark:text-red-400">
              {entry.denied ? <Ban className="h-3 w-3" /> : <XCircle className="h-3 w-3" />}
              <span className="font-mono">{entry.name}</span>
            </div>
            <div className="mt-1 break-words text-[9px] leading-relaxed text-red-600/90 dark:text-red-400/90">{entry.message}</div>
          </div>
        )
      })}
    </div>
  )
}

function MessageBody({ message }: { message: ChatMessage }) {
  if (message.role === 'assistant') {
    return (
      <div className="markdown-body">
        <ReactMarkdown remarkPlugins={[remarkGfm]}>{message.content ?? ''}</ReactMarkdown>
      </div>
    )
  }
  if (message.role === 'tool') {
    return <ProgressBody content={message.content ?? ''} />
  }
  return (
    <div className="whitespace-pre-wrap break-words text-[10px] leading-relaxed">{message.content ?? ''}</div>
  )
}

function MessageList({ messages, busy }: { messages: ChatMessage[], busy: boolean }) {
  const visibleMessages = messages.filter(message => message.role !== 'system')
  if (visibleMessages.length === 0) {
    return (
      <div className="grid h-full place-items-center p-6 text-center text-[10px] text-muted-foreground">
        <div>
          <MessageSquare className="mx-auto mb-2 h-5 w-5" />
          Start with a question for this device context.
        </div>
      </div>
    )
  }
  return (
    <div className="space-y-3 p-4">
      {visibleMessages.map((message, index) => (
        <div
          key={`${message.role}-${index}`}
          className={`rounded-lg border p-3 ${messageTone(message)}`}
          style={{ borderColor: 'var(--border)' }}
        >
          <div className="mb-1 text-[8px] uppercase tracking-[0.15em] text-muted-foreground">{roleLabel(message)}</div>
          {message.reasoningContent && (
            <pre className="mb-2 whitespace-pre-wrap rounded-md bg-muted/40 p-2 text-[9px] text-muted-foreground">{message.reasoningContent}</pre>
          )}
          <MessageBody message={message} />
        </div>
      ))}
      {busy && <BusyRow />}
    </div>
  )
}

export default function ChatWorkspace({ tabs, busy, onFocus, onSend, onStop, onContinue }: Props) {
  const [settings, setSettings] = useState<api.ChatSettings | null>(null)
  const [settingsState, setSettingsState] = useState<SettingsSaveState>('idle')
  const settingsRef = useRef<api.ChatSettings | null>(null)
  const saveTimer = useRef<number | undefined>(undefined)

  useEffect(() => {
    let cancelled = false
    api.getChatSettings()
      .then(loaded => {
        if (cancelled) return
        settingsRef.current = loaded
        setSettings(loaded)
      })
      .catch(() => { if (!cancelled) setSettingsState('error') })
    return () => {
      cancelled = true
      window.clearTimeout(saveTimer.current)
    }
  }, [])

  const changeSettings = useCallback((patch: Partial<api.ChatSettings>) => {
    const base = settingsRef.current
    if (!base) return
    const next = { ...base, ...patch }
    settingsRef.current = next
    setSettings(next)
    setSettingsState('saving')
    window.clearTimeout(saveTimer.current)
    saveTimer.current = window.setTimeout(() => {
      api.saveChatSettings(next)
        .then(() => setSettingsState('saved'))
        .catch(() => setSettingsState('error'))
    }, 400)
  }, [])

  if (tabs.tabs.length === 0) {
    return (
      <div className="grid h-full place-items-center p-8 text-center">
        <div className="max-w-sm">
          <MessageSquare className="mx-auto mb-3 h-7 w-7 text-chart-3" />
          <h2 className="text-sm font-semibold">No chat session open</h2>
          <p className="mt-1 text-[10px] leading-relaxed text-muted-foreground">
            Use the session dock to start a new chat or resume a saved one.
          </p>
        </div>
      </div>
    )
  }

  return (
    <div className="flex h-full min-h-0 flex-col">
      <div className="scrollbar-sleek flex h-9 shrink-0 items-center gap-1 overflow-x-auto border-b px-2" style={{ borderColor: 'var(--border)' }}>
        {tabs.tabs.map(tab => (
          <button
            key={tab.sessionId}
            className={`h-7 max-w-[180px] truncate rounded-md px-2 text-[9px] ${tab.sessionId === tabs.activeId ? 'bg-accent text-foreground' : 'text-muted-foreground hover:bg-accent/50'}`}
            disabled={busy}
            onClick={() => onFocus(tab.sessionId)}
          >
            {tab.title}
          </button>
        ))}
      </div>
      <div className="min-h-0 flex-1">
        {tabs.tabs.map(tab => (
          <section
            key={tab.sessionId}
            data-session-pane={tab.sessionId}
            hidden={tab.sessionId !== tabs.activeId}
            className="h-full min-h-0"
          >
            <div className="flex h-full min-h-0 flex-col">
              <div className="scrollbar-sleek min-h-0 flex-1 overflow-y-auto">
                <MessageList messages={tab.messages} busy={busy && tab.sessionId === tabs.activeId} />
              </div>
              {tab.hitRoundCap && (
                <div className="border-t px-3 pt-2" style={{ borderColor: 'var(--border)' }} data-round-cap={tab.sessionId}>
                  <button
                    type="button"
                    className="secondary-button w-full"
                    disabled={busy}
                    onClick={() => onContinue(tab.sessionId)}
                  >
                    Round limit reached — Continue (+6 rounds)
                  </button>
                </div>
              )}
              <ChatComposer
                sessionId={tab.sessionId}
                disabled={busy || tab.sessionId !== tabs.activeId}
                busy={busy && tab.sessionId === tabs.activeId}
                settings={settings}
                settingsState={settingsState}
                usage={tab.usage}
                onSettingsChange={changeSettings}
                onSend={onSend}
                onStop={onStop}
              />
            </div>
          </section>
        ))}
      </div>
    </div>
  )
}
