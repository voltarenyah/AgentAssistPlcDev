import { Ban, Loader2, MessageSquare, Send, Wrench, XCircle } from 'lucide-react'
import ReactMarkdown from 'react-markdown'
import remarkGfm from 'remark-gfm'
import type { ChatMessage } from '@/api/client'
import type { ChatTabsState } from './chatTabState'
import { parseProgressContent, progressTitle } from './progressDisplay'

type Props = {
  tabs: ChatTabsState
  busy: boolean
  onFocus: (sessionId: string) => void
  onSend: (sessionId: string, message: string) => void
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

function ChatComposer({
  sessionId,
  disabled,
  busy,
  onSend,
}: {
  sessionId: string
  disabled: boolean
  busy: boolean
  onSend: (sessionId: string, message: string) => void
}) {
  return (
    <form
      data-chat-composer={sessionId}
      className="flex gap-2 border-t p-3"
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
      <textarea
        name="message"
        className="field-input min-h-16 flex-1 resize-none py-2"
        disabled={disabled}
        placeholder="Ask about this PLC device..."
      />
      <button className="primary-button h-16 px-3" disabled={disabled} aria-label="Send message">
        {busy ? <Loader2 className="h-3.5 w-3.5 animate-spin" /> : <Send className="h-3.5 w-3.5" />}
      </button>
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

export default function ChatWorkspace({ tabs, busy, onFocus, onSend }: Props) {
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
              <ChatComposer
                sessionId={tab.sessionId}
                disabled={busy || tab.sessionId !== tabs.activeId}
                busy={busy && tab.sessionId === tabs.activeId}
                onSend={onSend}
              />
            </div>
          </section>
        ))}
      </div>
    </div>
  )
}
