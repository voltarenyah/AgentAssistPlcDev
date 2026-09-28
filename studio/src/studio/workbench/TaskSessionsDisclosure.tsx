import { useState } from 'react'
import { ChevronDown, MessageSquareText } from 'lucide-react'
import type { ChatSessionInfo } from '@/api/client'
import { Button } from '@/components/ui/button'
import { Collapsible, CollapsibleContent, CollapsibleTrigger } from '@/components/ui/collapsible'

export function formatRelativeTime(value: string, now = Date.now()) {
  const timestamp = Date.parse(value)
  if (!Number.isFinite(timestamp)) return 'Time unavailable'
  const seconds = Math.round((timestamp - now) / 1000)
  if (Math.abs(seconds) < 60) return 'just now'

  const units: Array<[number, Intl.RelativeTimeFormatUnit]> = [
    [60 * 60 * 24 * 365, 'year'],
    [60 * 60 * 24 * 30, 'month'],
    [60 * 60 * 24 * 7, 'week'],
    [60 * 60 * 24, 'day'],
    [60 * 60, 'hour'],
    [60, 'minute'],
  ]
  const [divisor, unit] = units.find(([size]) => Math.abs(seconds) >= size) ?? [60, 'minute']
  return new Intl.RelativeTimeFormat('en', { numeric: 'auto' }).format(Math.round(seconds / divisor), unit)
}

type Props = {
  taskTitle: string
  sessions: ChatSessionInfo[]
  onOpenSession?: (sessionId: string) => void
}

export default function TaskSessionsDisclosure({ taskTitle, sessions, onOpenSession }: Props) {
  const [open, setOpen] = useState(false)

  return (
    <Collapsible open={open} onOpenChange={setOpen} className="border-t">
      <div className="px-3 py-1.5">
        <CollapsibleTrigger asChild>
          <Button type="button" variant="ghost" size="xs" aria-label={`${open ? 'Hide' : 'Show'} ${sessions.length} sessions for ${taskTitle}`} aria-expanded={open}>
            {open ? 'Hide sessions' : `Show ${sessions.length} sessions`}
            <ChevronDown className={`h-3.5 w-3.5 transition-transform ${open ? 'rotate-180' : ''}`} />
          </Button>
        </CollapsibleTrigger>
      </div>
      <CollapsibleContent className="border-t bg-muted/30 px-3 py-2">
        {sessions.length === 0 ? <p className="px-1 py-2 text-xs text-muted-foreground">No conversations linked to this task yet.</p> : (
          <ul className="divide-y divide-border">
            {sessions.map(session => <li key={session.sessionId} className="flex min-w-0 items-center gap-2 py-2 first:pt-1 last:pb-1">
              <MessageSquareText className="mt-0.5 h-3.5 w-3.5 shrink-0 text-muted-foreground" />
              <span className="min-w-0 flex-1 truncate text-xs font-medium" title={session.title || session.firstUserMessage || 'Untitled conversation'}>{session.title?.trim() || session.firstUserMessage?.trim() || 'Untitled conversation'}</span>
              <time className="shrink-0 text-[10px] text-muted-foreground" dateTime={session.updatedAt} title={session.updatedAt ? new Date(session.updatedAt).toLocaleString() : undefined}>
                {formatRelativeTime(session.updatedAt)}
              </time>
              {onOpenSession && <Button type="button" variant="outline" size="xs" aria-label={`Open conversation ${session.title?.trim() || session.sessionId}`} onClick={() => onOpenSession(session.sessionId)}>Open</Button>}
            </li>)}
          </ul>
        )}
      </CollapsibleContent>
    </Collapsible>
  )
}
