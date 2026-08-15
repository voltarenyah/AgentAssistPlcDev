import { useState } from 'react'
import { Link2, Link2Off, Loader2, RefreshCw, Server, X, XCircle } from 'lucide-react'
import type * as api from '@/api/client'

/** Compact mode chip: TIA's enum names are verbose ("WithUserInterface"). */
export const sessionModeLabel = (mode: string): string => {
  if (/^without|headless/i.test(mode)) return 'Headless'
  if (/^with/i.test(mode)) return 'UI'
  return mode
}

/** "Demo / feature-x" from the resolved label, with graceful fallbacks. */
export type SessionLabel = {
  project: string | null
  worktree: string | null
}

export const formatSessionLabel = (label: SessionLabel | null): string => {
  if (!label || !label.project) return 'No project open'
  return label.worktree ? `${label.project} / ${label.worktree}` : label.project
}

export default function TiaSessionsPanel({
  sessions,
  current,
  busy,
  resolveLabel,
  onRefresh,
  onAttach,
  onDetach,
  onCloseSession,
  onClose,
}: {
  sessions: api.SessionInfo[]
  current: api.CurrentTiaSession | null
  busy: string | null
  resolveLabel?: (session: api.SessionInfo) => SessionLabel | null
  onRefresh: () => void
  onAttach: (sessionId: number) => void
  onDetach: () => void
  onCloseSession: (sessionId: number) => void
  onClose: () => void
}) {
  const [confirmCloseId, setConfirmCloseId] = useState<number | null>(null)
  const refreshing = busy === 'refresh'

  return (
    <div
      className="absolute bottom-7 right-0 z-50 w-[360px] overflow-hidden rounded-md border bg-card text-foreground shadow-xl"
      style={{ borderColor: 'var(--border)' }}
      data-tia-sessions-panel
    >
      <div
        className="flex items-center gap-1.5 border-b px-2.5 py-1.5 text-[9px] font-semibold uppercase tracking-[0.12em] text-muted-foreground"
        style={{ borderColor: 'var(--border)' }}
      >
        <Server className="h-3 w-3" />
        <span className="flex-1">TIA Portal instances</span>
        <button
          className="icon-button h-5 w-5"
          aria-label="Refresh TIA instances"
          title="Re-detect running TIA Portal instances"
          disabled={refreshing}
          onClick={onRefresh}
        >
          <RefreshCw className={`h-3 w-3 ${refreshing ? 'animate-spin' : ''}`} />
        </button>
        <button className="icon-button h-5 w-5" aria-label="Close panel" onClick={onClose}>
          <X className="h-3 w-3" />
        </button>
      </div>

      <div className="scrollbar-sleek max-h-[280px] overflow-y-auto">
        {sessions.length === 0 && (
          <p className="px-3 py-5 text-center text-[10px] text-muted-foreground">
            No running TIA Portal instances detected. Open TIA Portal, then refresh.
          </p>
        )}
        {sessions.map(session => {
          const isAttached = current?.attached === true && current.sessionId === session.id
          const label = formatSessionLabel(resolveLabel?.(session) ?? null)
          const fullPath = session.projectPath ?? session.portalPath ?? undefined
          return (
            <div
              key={session.id}
              data-tia-session={session.id}
              className={`flex items-center gap-2 border-b px-2.5 py-2 last:border-b-0 ${isAttached ? 'bg-emerald-500/5' : ''}`}
              style={{ borderColor: 'var(--border)' }}
            >
              <div className="min-w-0 flex-1">
                <div className="flex items-center gap-1.5">
                  <span className="font-mono text-[10px] font-medium">PID {session.id}</span>
                  <span className="rounded-full bg-muted px-1.5 py-px text-[9px] text-muted-foreground">
                    {sessionModeLabel(session.mode)}
                  </span>
                  {isAttached && (
                    <span className="rounded-full bg-emerald-500/15 px-1.5 py-px text-[9px] font-medium text-emerald-500">
                      Attached
                    </span>
                  )}
                </div>
                <div className="mt-0.5 truncate text-[9px] text-muted-foreground" title={fullPath}>
                  {label}
                </div>
              </div>
              <div className="flex shrink-0 items-center gap-1">
                {isAttached ? (
                  <button
                    className="inline-flex h-6 items-center gap-1 rounded-md bg-emerald-600 px-2 text-[9px] font-medium text-white transition-opacity hover:opacity-90 disabled:opacity-40"
                    disabled={busy !== null}
                    onClick={onDetach}
                    data-tia-detach
                  >
                    {busy === 'detach' ? <Loader2 className="h-3 w-3 animate-spin" /> : <Link2Off className="h-3 w-3" />}
                    Detach
                  </button>
                ) : (
                  <button
                    className="inline-flex h-6 items-center gap-1 rounded-md bg-primary px-2 text-[9px] font-medium text-primary-foreground transition-opacity hover:opacity-90 disabled:opacity-40"
                    disabled={busy !== null}
                    onClick={() => onAttach(session.id)}
                    data-tia-attach={session.id}
                  >
                    {busy === `attach:${session.id}` ? <Loader2 className="h-3 w-3 animate-spin" /> : <Link2 className="h-3 w-3" />}
                    Attach
                  </button>
                )}
                {confirmCloseId === session.id ? (
                  <>
                    <button
                      className="inline-flex h-6 items-center gap-1 rounded-md bg-red-600 px-2 text-[9px] font-medium text-white transition-opacity hover:opacity-90 disabled:opacity-40"
                      disabled={busy !== null}
                      onClick={() => { setConfirmCloseId(null); onCloseSession(session.id) }}
                      data-tia-close-confirm={session.id}
                    >
                      {busy === `close:${session.id}` ? <Loader2 className="h-3 w-3 animate-spin" /> : null}
                      Confirm
                    </button>
                    <button
                      className="inline-flex h-6 items-center rounded-md border px-2 text-[9px] text-muted-foreground transition-colors hover:bg-accent"
                      style={{ borderColor: 'var(--border)' }}
                      onClick={() => setConfirmCloseId(null)}
                    >
                      Keep
                    </button>
                  </>
                ) : (
                  <button
                    className="icon-button h-6 w-6 hover:text-red-500"
                    aria-label={`Close TIA instance ${session.id}`}
                    title="Close this TIA Portal instance (TIA asks to save changes)"
                    disabled={busy !== null}
                    onClick={() => setConfirmCloseId(session.id)}
                    data-tia-close={session.id}
                  >
                    <XCircle className="h-3.5 w-3.5" />
                  </button>
                )}
              </div>
            </div>
          )
        })}
      </div>

      <div
        className="border-t px-2.5 py-1.5 text-[9px] text-muted-foreground"
        style={{ borderColor: 'var(--border)' }}
      >
        Detach keeps the project open for re-attach; close shuts the instance down.
      </div>
    </div>
  )
}
