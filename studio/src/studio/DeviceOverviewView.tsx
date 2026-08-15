import {
  AlertCircle,
  ArrowDownToLine,
  CircleDot,
  Cpu,
  Database,
  GitMerge,
  RefreshCw,
  RotateCw,
  Server,
  ShieldCheck,
  Sparkles,
} from 'lucide-react'
import type {
  ChatSessionInfo,
  DeviceExportMetadata,
  DeviceInfo,
  KnowledgeVisualState,
  OfflineBlockInfo,
  SessionInfo,
  WorkbenchRegistration,
} from '@/api/client'
import type { DeviceViewState } from '@/studio/deviceSnapshot'

export type DeviceOverviewViewProps = {
  deviceName: string | null
  deviceId: string | null
  deviceInfo: DeviceInfo | null
  deviceMeta: DeviceExportMetadata | null
  deviceView: DeviceViewState | null
  blocks: OfflineBlockInfo[]
  displayedSourceObjectCount: number
  deviceSessions: ChatSessionInfo[]
  activeKnowledge: KnowledgeVisualState
  isBrandNewDevice: boolean
  matchingTiaSession: SessionInfo | null
  operation: string | null
  rebuildArmed: boolean
  setRebuildArmed: (armed: boolean) => void
  activeWorktree: WorkbenchRegistration | null
  onOpenProjectInTia: () => void
  onAttachTiaInstance: (sessionId: number) => void
  onStageRefresh: () => void
  onRebuildProject: () => void
  onUpdateKnowledge: (rebuild: boolean) => void
  onMergeIntoMaster: () => void
  onBootstrapDevice: () => void
}

function Metric({
  label,
  value,
  tone = 'neutral',
}: {
  label: string
  value: string | number
  tone?: 'neutral' | 'good' | 'warning' | 'danger'
}) {
  const color = tone === 'good' ? 'text-emerald-500'
    : tone === 'warning' ? 'text-amber-500'
      : tone === 'danger' ? 'text-red-500'
        : 'text-foreground'
  return (
    <div className="rounded-lg border bg-card p-3" style={{ borderColor: 'var(--border)' }}>
      <div className={`text-xl font-semibold tabular-nums ${color}`}>{value}</div>
      <div className="mt-1 text-[9px] uppercase tracking-[0.15em] text-muted-foreground">{label}</div>
    </div>
  )
}

export default function DeviceOverviewView({
  deviceName,
  deviceId,
  deviceInfo,
  deviceMeta,
  deviceView,
  blocks,
  displayedSourceObjectCount,
  deviceSessions,
  activeKnowledge,
  isBrandNewDevice,
  matchingTiaSession,
  operation,
  rebuildArmed,
  setRebuildArmed,
  activeWorktree,
  onOpenProjectInTia,
  onAttachTiaInstance,
  onStageRefresh,
  onRebuildProject,
  onUpdateKnowledge,
  onMergeIntoMaster,
  onBootstrapDevice,
}: DeviceOverviewViewProps) {
  return (
    <div className="mx-auto max-w-6xl space-y-5 p-5">
      <section className="flex flex-wrap items-start gap-4 rounded-xl border bg-card p-5" style={{ borderColor: 'var(--border)' }}>
        <div className="grid h-12 w-12 place-items-center rounded-xl bg-chart-2/10">
          <Cpu className="h-5 w-5 text-chart-2" />
        </div>
        <div className="min-w-0 flex-1">
          <h1 className="text-lg font-semibold">{deviceName}</h1>
          <p className="mt-0.5 font-mono text-[9px] text-muted-foreground">{deviceInfo?.engineeringIdentity ?? deviceId}</p>
          {(deviceMeta?.typeIdentifier || deviceMeta?.deviceName) && (
            <p className="mt-1.5 flex items-center gap-1.5 font-mono text-[10px] text-muted-foreground">
              <Cpu className="h-3 w-3" />
              {deviceMeta.typeIdentifier?.replace(/^OrderNumber:/, '') ?? ''}
              {deviceMeta.typeIdentifier && deviceMeta.deviceName ? ' · ' : ''}
              {deviceMeta.deviceName ?? ''}
            </p>
          )}
          <div className="mt-3 flex flex-wrap gap-2">
            <button className="secondary-button" disabled={Boolean(operation)} onClick={() => onOpenProjectInTia()}>
              <Server className="h-3.5 w-3.5" /> Open project in TIA
            </button>
            {matchingTiaSession && (
              <button className="secondary-button" disabled={Boolean(operation)} onClick={() => onAttachTiaInstance(matchingTiaSession.id)}>
                <Server className="h-3.5 w-3.5" /> Re-attach TIA instance (PID {matchingTiaSession.id})
              </button>
            )}
            <button className="primary-button" disabled={Boolean(operation)} onClick={() => onStageRefresh()}>
              <RefreshCw className="h-3.5 w-3.5" /> Compare with TIA
            </button>
            {!isBrandNewDevice && (
              <button
                className={rebuildArmed ? 'primary-button' : 'secondary-button'}
                disabled={Boolean(operation)}
                onClick={() => {
                  if (!rebuildArmed) {
                    setRebuildArmed(true)
                    setTimeout(() => setRebuildArmed(false), 4000)
                    return
                  }
                  setRebuildArmed(false)
                  onRebuildProject()
                }}
              >
                <RotateCw className="h-3.5 w-3.5" /> {rebuildArmed ? 'Confirm full rebuild?' : 'Rebuild project'}
              </button>
            )}
            <button className="secondary-button" disabled={Boolean(operation)} onClick={() => onUpdateKnowledge(false)}>
              <Database className="h-3.5 w-3.5" /> Update knowledge
            </button>
            {activeWorktree?.branch !== 'master' && (
              <button className="secondary-button" disabled={Boolean(operation)} onClick={() => onMergeIntoMaster()}>
                <GitMerge className="h-3.5 w-3.5" /> Merge to master
              </button>
            )}
          </div>
        </div>
        <div className="rounded-lg border px-3 py-2" style={{ borderColor: 'var(--border)' }}>
          <div className="flex items-center gap-2 text-[8px] uppercase tracking-[0.16em] text-muted-foreground">
            <span>Knowledge</span>
            <span className="rounded-full bg-emerald-500/10 px-1.5 py-0.5 text-emerald-600 dark:text-emerald-400">Offline ready</span>
          </div>
          <div className={`mt-1 flex items-center gap-1.5 text-[10px] font-medium ${
            activeKnowledge === 'current' ? 'text-emerald-500'
              : activeKnowledge === 'stale' ? 'text-amber-500'
                : activeKnowledge === 'failed' ? 'text-red-500'
                  : 'text-muted-foreground'
          }`}>
            <Database className="h-3.5 w-3.5" /> {activeKnowledge}
          </div>
          <div className="mt-1 text-[8px] text-muted-foreground">
            Updated {deviceView?.knowledgeUpdatedAt
              ? new Date(deviceView.knowledgeUpdatedAt).toLocaleString()
              : 'never'}
          </div>
        </div>
      </section>

      {isBrandNewDevice && (
        <section className="flex flex-wrap items-center gap-4 rounded-xl border border-chart-2/40 bg-chart-2/5 p-5">
          <div className="grid h-10 w-10 place-items-center rounded-lg bg-chart-2/10">
            <Sparkles className="h-5 w-5 text-chart-2" />
          </div>
          <div className="min-w-0 flex-1">
            <h2 className="text-sm font-semibold">Start by generating the PLC context</h2>
            <p className="mt-1 text-[10px] leading-relaxed text-muted-foreground">
              Exports the full PLC from TIA, commits it as the initial baseline, and builds the offline knowledge database — no confirmations needed.
            </p>
          </div>
          <button className="primary-button" disabled={Boolean(operation)} onClick={() => onBootstrapDevice()}>
            <Sparkles className="h-3.5 w-3.5" /> Generate PLC context
          </button>
        </section>
      )}

      <div className="grid grid-cols-2 gap-3 lg:grid-cols-4">
        <Metric label="PLC blocks" value={blocks.length} />
        <Metric label="Source objects" value={displayedSourceObjectCount} />
        <Metric label="Saved sessions" value={deviceSessions.length} />
        <Metric label="Knowledge state" value={activeKnowledge} tone={activeKnowledge === 'current' ? 'good' : activeKnowledge === 'failed' ? 'danger' : 'warning'} />
      </div>

      <div className="grid gap-4 lg:grid-cols-2">
        <section className="rounded-xl border bg-card p-5" style={{ borderColor: 'var(--border)' }}>
          <div className="flex items-center gap-3">
            <Database className="h-5 w-5 text-chart-2" />
            <div>
              <h2 className="text-sm font-semibold">Device-owned knowledge</h2>
              <p className="text-[9px] text-muted-foreground">No cross-device lifecycle coupling</p>
            </div>
          </div>
          <div className="mt-5 rounded-lg border bg-muted/30 p-4" style={{ borderColor: 'var(--border)' }}>
            <div className="text-[8px] uppercase tracking-[0.16em] text-muted-foreground">State</div>
            <div className="mt-2 flex items-center gap-2 text-lg font-semibold capitalize">
              <CircleDot className={`h-4 w-4 ${activeKnowledge === 'current' ? 'text-emerald-500' : activeKnowledge === 'failed' ? 'text-red-500' : 'text-amber-500'}`} />
              {activeKnowledge}
            </div>
            <div className="mt-2 text-[9px] text-muted-foreground">
              Last updated: {deviceView?.knowledgeUpdatedAt
                ? new Date(deviceView.knowledgeUpdatedAt).toLocaleString()
                : 'Never'}
            </div>
          </div>
          {activeKnowledge !== 'current' && (
            <div className="mt-3 flex items-start gap-2 rounded-lg bg-amber-500/8 p-3 text-[9px] leading-relaxed text-amber-600 dark:text-amber-400">
              <AlertCircle className="mt-0.5 h-3.5 w-3.5 shrink-0" />
              Update once after your edit batch and before relying on graph or block context.
            </div>
          )}
        </section>
        <section className="rounded-xl border bg-card p-5" style={{ borderColor: 'var(--border)' }}>
          <h2 className="text-sm font-semibold">Maintenance actions</h2>
          <p className="mt-1 text-[10px] leading-relaxed text-muted-foreground">
            Normal update batches stale source objects. Rebuild ingests the full PLC source tree.
          </p>
          <div className="mt-5 space-y-2">
            <button className="primary-button w-full" disabled={Boolean(operation)} onClick={() => onUpdateKnowledge(false)}>
              <ArrowDownToLine className="h-3.5 w-3.5" /> Update changed components
            </button>
            <button className="secondary-button w-full" disabled={Boolean(operation)} onClick={() => onUpdateKnowledge(true)}>
              <RefreshCw className="h-3.5 w-3.5" /> Full device rebuild
            </button>
          </div>
          <div className="mt-5 flex items-center gap-2 text-[9px] text-muted-foreground">
            <ShieldCheck className="h-4 w-4 text-emerald-500" />
            Applied hashes are checked before stale state clears.
          </div>
        </section>
      </div>

    </div>
  )
}
