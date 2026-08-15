import { useState } from 'react'
import { Link2, Link2Off, Loader2, RefreshCw, Server, X, XCircle } from 'lucide-react'
import type * as api from '@/api/client'

const formatPath = (session: api.SessionInfo) => session.projectPath ?? session.portalPath ?? null

export default function TiaSessionsPanel({
  sessions,
  current,
  busy,
  onRefresh,
  onAttach,
  onDetach,
  onCloseSession,
  onClose,
}: {
  sessions: api.SessionInfo[]
  current: api.CurrentTiaSession | null
  busy: string | null
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
      className="fixed bottom-9 right-3 z-40 w-[400px] overflow-hidden rounded-xl border bg-card shadow-2xl"
      style={{ borderColor: 'var(--border)' }}
      data-tia-sessions-panel
    >
      <div className="flex items-center gap-2 border-b px-4 py-3" style={{ borderColor: 'var(--border)' }}>
        <Server className="h-3.5 w-3.5 text-chart-3" />
        <h2 className="flex-1 text-xs font-semibold">TIA Portal instances</h2>
        <button
          className="icon-button h-6 w-6"
          aria-label="Refresh TIA instances"
          title="Re-detect running TIA Portal instances"
          disabled={refreshing}
          onClick={onRefresh}
        >
          <RefreshCw className={`h-3 w-3 ${refreshing ? 'animate-spin' : ''}`} />
        </button>
        <button className="icon-button h-6 w-6" aria-label="Close panel" onClick={onClose}>
          <X className="h-3 w-3" />
        </button>
      </div>

      <div className="border-b px-4 py-3" style={{ borderColor: 'var(--border)' }} data-tia-current>
        {current?.attached ? (
          <div className="flex items-center justify-between gap-3">
            <div className="min-w-0">
              <div className="text-[11px] font-medium text-emerald-500">
                Attached to PID {current.sessionId ?? '—'}
              </div>
              <div className="truncate text-[10px] text-muted-foreground" title={current.projectPath ?? undefined}>
                {current.projectName ?? 'Unnamed project'}{current.projectPath ? ` · ${current.projectPath}` : ''}
              </div>
            </div>
            <button
              className="secondary-button h-7 shrink-0"
              disabled={busy !== null}
              onClick={onDetach}
              data-tia-detach
            >
              {busy === 'detach' ? <Loader2 className="h-3 w-3 animate-spin" /> : <Link2Off className="h-3 w-3" />}
              Detach
            </button>
          </div>
        ) : (
          <div className="text-[10px] text-muted-foreground">Not attached to any instance.</div>
        )}
      </div>

      <div className="scrollbar-sleek max-h-[300px] overflow-y-auto">
        {sessions.length === 0 && (
          <p className="px-4 py-6 text-center text-[10px] text-muted-foreground">
            No running TIA Portal instances detected. Open TIA Portal, then use the refresh button above.
          </p>
        )}
        {sessions.map(session => {
          const isAttached = current?.attached === true && current.sessionId === session.id
          const path = formatPath(session)
          return (
            <div
              key={session.id}
              data-tia-session={session.id}
              className={`flex items-center gap-3 px-4 py-3 ${isAttached ? 'bg-emerald-500/5' : ''}`}
            >
              <div className="min-w-0 flex-1">
                <div className="flex items-center gap-2">
                  <span className="font-mono text-[11px] font-medium">PID {session.id}</span>
                  <span className="rounded-full bg-muted px-1.5 py-0.5 text-[9px] text-muted-foreground">{session.mode}</span>
                  {isAttached && (
                    <span className="rounded-full bg-emerald-500/10 px-1.5 py-0.5 text-[9px] font-medium text-emerald-500">Attached</span>
                  )}
                </div>
                {path && <div className="mt-0.5 truncate font-mono text-[9px] text-muted-foreground" title={path}>{path}</div>}
              </div>
              <div className="flex shrink-0 items-center gap-1.5">
                {!isAttached && (
                  <button
                    className="secondary-button h-7"
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
                      className="primary-button h-7 bg-red-600"
                      disabled={busy !== null}
                      onClick={() => { setConfirmCloseId(null); onCloseSession(session.id) }}
                      data-tia-close-confirm={session.id}
                    >
                      {busy === `close:${session.id}` ? <Loader2 className="h-3 w-3 animate-spin" /> : null}
                      Confirm
                    </button>
                    <button className="secondary-button h-7" onClick={() => setConfirmCloseId(null)}>Keep</button>
                  </>
                ) : (
                  <button
                    className="icon-button h-7 w-7 hover:text-red-500"
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

      <div className="border-t px-4 py-2 text-[9px] text-muted-foreground" style={{ borderColor: 'var(--border)' }}>
        Close sends the window close signal — TIA Portal asks whether to save changes.
      </div>
    </div>
  )
}
