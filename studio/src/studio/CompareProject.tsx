import { useState, useEffect, useCallback } from 'react'
import { Loader2, RefreshCw, FileCheck, FileX, FilePlus, AlertTriangle, Cpu } from 'lucide-react'
import * as api from '@/api/client'

/* ── Helpers ──────────────────────────────────────────── */

function stateIcon(state: string) {
  switch (state) {
    case 'same':         return <FileCheck className="h-3.5 w-3.5" style={{ color: '#22c55e' }} />
    case 'different':    return <FileX className="h-3.5 w-3.5" style={{ color: '#ef4444' }} />
    case 'new':          return <FilePlus className="h-3.5 w-3.5" style={{ color: '#06b6d4' }} />
    case 'missing':      return <FileX className="h-3.5 w-3.5" style={{ color: '#f59e0b' }} />
    case 'unverifiable': return <AlertTriangle className="h-3.5 w-3.5" style={{ color: '#a78bfa' }} />
    default:             return <AlertTriangle className="h-3.5 w-3.5" style={{ color: 'var(--muted-foreground)' }} />
  }
}

function stateLabel(state: string) {
  switch (state) {
    case 'same':         return 'Same'
    case 'different':    return 'Different'
    case 'new':          return 'New (live only)'
    case 'missing':      return 'Missing (manifest only)'
    case 'unverifiable': return 'Unverifiable'
    default:             return 'Unknown'
  }
}

function checksumStateClass(state: string) {
  switch (state) {
    case 'in-sync': return '#22c55e'
    case 'changed': return '#ef4444'
    case 'no-baseline': return '#f59e0b'
    default: return 'var(--muted-foreground)'
  }
}

/* ── Props ────────────────────────────────────────────── */

interface CompareProjectProps {
  projectName?: string | null
  plcName?: string
}

/* ── Component ────────────────────────────────────────── */

export default function CompareProject({ projectName, plcName }: CompareProjectProps) {
  const [compareResult, setCompareResult] = useState<api.ContextCompareResult | null>(null)
  const [statusResult, setStatusResult] = useState<api.ContextStatusResult | null>(null)
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)

  const fetchData = useCallback(async () => {
    if (!projectName) {
      setLoading(false)
      return
    }
    setLoading(true)
    setError(null)
    try {
      const [compare, status] = await Promise.all([
        api.compareContext(undefined, plcName),
        api.getContextStatus(undefined, plcName),
      ])
      setCompareResult(compare)
      setStatusResult(status)
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Failed to load compare data')
    } finally {
      setLoading(false)
    }
  }, [projectName])

  useEffect(() => { fetchData() }, [fetchData])

  /* ── No project selected ───────────────────────────── */
  if (!projectName) {
    return (
      <div className="flex flex-1 flex-col items-center justify-center gap-2 text-center">
        <Cpu className="h-8 w-8" style={{ color: 'var(--muted-foreground)' }} />
        <span className="text-sm font-medium" style={{ color: 'var(--foreground)' }}>
          No project selected
        </span>
        <span className="text-xs max-w-sm" style={{ color: 'var(--muted-foreground)' }}>
          Right-click a PLC device and select "Compare Project" to view diff data for that device.
        </span>
      </div>
    )
  }

  /* ── Loading ───────────────────────────────────────── */
  if (loading) {
    return (
      <div className="flex flex-1 flex-col items-center justify-center gap-3">
        <Loader2 className="h-6 w-6 animate-spin" style={{ color: 'var(--primary)' }} />
        <span className="text-xs" style={{ color: 'var(--muted-foreground)' }}>
          Loading comparison data...
        </span>
      </div>
    )
  }

  /* ── Error ─────────────────────────────────────────── */
  if (error) {
    return (
      <div className="flex flex-1 flex-col items-center justify-center gap-3 px-6">
        <AlertTriangle className="h-8 w-8" style={{ color: 'var(--destructive)' }} />
        <span className="text-sm font-medium" style={{ color: 'var(--foreground)' }}>
          Failed to load comparison
        </span>
        <span className="text-xs text-center max-w-md" style={{ color: 'var(--muted-foreground)' }}>
          {error}
        </span>
        <button
          onClick={fetchData}
          className="rounded-md px-4 py-2 text-xs font-medium hover:bg-accent"
          style={{ background: 'var(--card)', color: 'var(--foreground)' }}
        >
          Retry
        </button>
      </div>
    )
  }

  /* ── No manifest / no data ─────────────────────────── */
  if (!compareResult || !compareResult.manifestExists) {
    return (
      <div className="flex flex-1 flex-col items-center justify-center gap-3 px-6">
        <FilePlus className="h-8 w-8" style={{ color: 'var(--muted-foreground)' }} />
        <span className="text-sm font-medium" style={{ color: 'var(--foreground)' }}>
          No context data found
        </span>
        <span className="text-xs text-center max-w-md" style={{ color: 'var(--muted-foreground)' }}>
          {statusResult?.manifestExists === false
            ? 'This project has not been exported yet. Run an export first to establish a baseline.'
            : 'No comparison data available for this project.'
          }
        </span>
        <button
          onClick={fetchData}
          className="rounded-md px-4 py-2 text-xs font-medium hover:bg-accent"
          style={{ background: 'var(--card)', color: 'var(--foreground)' }}
        >
          Refresh
        </button>
      </div>
    )
  }

  const checksumMatch = compareResult.liveChecksum === compareResult.storedChecksum
  const checksumStatus = !compareResult.storedChecksum
    ? 'no-baseline'
    : !compareResult.liveChecksum
      ? 'unknown'
      : checksumMatch
        ? 'in-sync'
        : 'changed'

  return (
    <div className="flex flex-1 flex-col overflow-hidden">
      {/* ── Toolbar ──────────────────────────────────── */}
      <div className="flex h-7 shrink-0 items-center gap-2 border-b px-2.5 text-[10px]"
        style={{ background: 'var(--card)', color: 'var(--muted-foreground)' }}>
        <Cpu className="h-3.5 w-3.5" style={{ color: 'var(--chart-3)' }} />
        <span className="font-medium" style={{ color: 'var(--foreground)' }}>
          {plcName || compareResult.plcName || projectName}
        </span>
        <span className="text-[9px]" style={{ color: 'var(--muted-foreground)' }}>
          {compareResult.exportRoot}
        </span>
        <div className="flex-1" />
        <button
          onClick={fetchData}
          className="flex h-6 w-6 cursor-default items-center justify-center rounded hover:bg-accent"
          title="Refresh comparison"
        >
          <RefreshCw className="h-3.5 w-3.5" style={{ color: 'var(--muted-foreground)' }} />
        </button>
      </div>

      {/* ── Scrollable content ─────────────────────────── */}
      <div className="flex-1 overflow-y-auto scrollbar-sleek">
        <div className="p-4 space-y-5 max-w-4xl mx-auto">

          {/* ── Checksum Comparison (side-by-side) ──────── */}
          <section>
            <h2 className="text-[11px] font-semibold mb-2" style={{ color: 'var(--foreground)' }}>
              Project Checksum
            </h2>
            <div className="grid grid-cols-2 gap-3">
              {/* Stored */}
              <div className="rounded-lg border p-3"
                style={{ background: 'var(--card)', borderColor: 'var(--border)' }}>
                <div className="flex items-center gap-1.5 mb-1">
                  <span className="h-2 w-2 rounded-full bg-blue-500" />
                  <span className="text-[10px] font-medium" style={{ color: 'var(--foreground)' }}>
                    Stored (Manifest)
                  </span>
                </div>
                <div className="font-mono text-[10px] break-all select-text"
                  style={{ color: compareResult.storedChecksum ? 'var(--foreground)' : 'var(--muted-foreground)' }}>
                  {compareResult.storedChecksum || '—'}
                </div>
                <div className="text-[9px] mt-1" style={{ color: 'var(--muted-foreground)' }}>
                  {compareResult.storedChecksum ? `${compareResult.components.length} components` : 'No baseline'}
                </div>
              </div>

              {/* Live */}
              <div className="rounded-lg border p-3"
                style={{ background: 'var(--card)', borderColor: 'var(--border)' }}>
                <div className="flex items-center gap-1.5 mb-1">
                  <span className="h-2 w-2 rounded-full"
                    style={{ background: checksumStatus === 'in-sync' ? '#22c55e' : checksumStatus === 'changed' ? '#ef4444' : '#f59e0b' }} />
                  <span className="text-[10px] font-medium" style={{ color: 'var(--foreground)' }}>
                    Live (TIA)
                  </span>
                </div>
                <div className="font-mono text-[10px] break-all select-text"
                  style={{ color: compareResult.liveChecksum ? 'var(--foreground)' : 'var(--muted-foreground)' }}>
                  {compareResult.liveChecksum || '—'}
                </div>
                <div className="text-[9px] mt-1 flex items-center gap-1.5"
                  style={{ color: checksumStateClass(checksumStatus) }}>
                  <span>●</span>
                  <span>
                    {checksumStatus === 'in-sync' && 'In sync'}
                    {checksumStatus === 'changed' && 'Changed'}
                    {checksumStatus === 'no-baseline' && 'No baseline'}
                    {checksumStatus === 'unknown' && 'Status unknown'}
                  </span>
                </div>
              </div>
            </div>
          </section>

          {/* ── Component Fingerprint Summary ──────────── */}
          {compareResult.components.length > 0 && (
            <section>
              <div className="flex items-center gap-2 mb-2">
                <h2 className="text-[11px] font-semibold" style={{ color: 'var(--foreground)' }}>
                  Components
                </h2>
                <span className="text-[9px]" style={{ color: 'var(--muted-foreground)' }}>
                  {compareResult.components.length} total
                </span>
                {/* Summary counts */}
                <div className="flex gap-2 ml-2 text-[9px]" style={{ color: 'var(--muted-foreground)' }}>
                  <span style={{ color: '#22c55e' }}>
                    {compareResult.components.filter(c => c.state === 'same').length} same
                  </span>
                  <span style={{ color: '#ef4444' }}>
                    {compareResult.components.filter(c => c.state === 'different').length} changed
                  </span>
                  <span style={{ color: '#06b6d4' }}>
                    {compareResult.components.filter(c => c.state === 'new').length} new
                  </span>
                  <span style={{ color: '#f59e0b' }}>
                    {compareResult.components.filter(c => c.state === 'missing').length} missing
                  </span>
                </div>
              </div>

              {/* ── Component table ─────────────────────── */}
              <div className="rounded-lg border overflow-hidden"
                style={{ background: 'var(--card)', borderColor: 'var(--border)' }}>
                {/* Table header */}
                <div className="grid grid-cols-12 gap-1 px-3 py-2 text-[9px] font-medium border-b"
                  style={{ background: 'var(--background)', color: 'var(--muted-foreground)', borderColor: 'var(--border)' }}>
                  <div className="col-span-1" />
                  <div className="col-span-3">Name</div>
                  <div className="col-span-2">Category</div>
                  <div className="col-span-2">State</div>
                  <div className="col-span-2">Stored Fingerprint</div>
                  <div className="col-span-2">Live Fingerprint</div>
                </div>

                {/* Table rows */}
                {compareResult.components.map((comp, i) => (
                  <div key={`${comp.name}-${i}`}
                    className="grid grid-cols-12 gap-1 px-3 py-2 text-[10px] border-b hover:bg-accent/30 items-center"
                    style={{ borderColor: 'var(--border)' }}>
                    <div className="col-span-1 flex items-center">
                      {stateIcon(comp.state)}
                    </div>
                    <div className="col-span-3 truncate font-medium" style={{ color: 'var(--foreground)' }}
                      title={comp.sourcePath}>
                      {comp.name}
                    </div>
                    <div className="col-span-2 truncate" style={{ color: 'var(--muted-foreground)' }}>
                      {comp.category}
                    </div>
                    <div className="col-span-2">
                      <span className="rounded px-1 py-0.5 text-[8px] font-medium"
                        style={{
                          background: comp.state === 'same' ? 'rgba(34,197,94,0.15)' :
                            comp.state === 'different' ? 'rgba(239,68,68,0.15)' :
                            comp.state === 'new' ? 'rgba(6,182,212,0.15)' :
                            comp.state === 'missing' ? 'rgba(245,158,11,0.15)' : 'var(--accent)',
                          color: comp.state === 'same' ? '#22c55e' :
                            comp.state === 'different' ? '#ef4444' :
                            comp.state === 'new' ? '#06b6d4' :
                            comp.state === 'missing' ? '#f59e0b' : 'var(--muted-foreground)',
                        }}>
                        {stateLabel(comp.state)}
                      </span>
                    </div>
                    <div className="col-span-2 font-mono text-[8px] truncate select-text"
                      style={{ color: comp.storedFingerprints ? 'var(--foreground)' : 'var(--muted-foreground)' }}>
                      {comp.storedFingerprints || '—'}
                    </div>
                    <div className="col-span-2 font-mono text-[8px] truncate select-text"
                      style={{ color: comp.liveFingerprints ? 'var(--foreground)' : 'var(--muted-foreground)' }}>
                      {comp.liveFingerprints || '—'}
                    </div>
                  </div>
                ))}
              </div>
            </section>
          )}

          {/* ── No components ──────────────────────────── */}
          {compareResult.components.length === 0 && (
            <div className="flex flex-col items-center justify-center py-12 gap-2">
              <FileCheck className="h-6 w-6" style={{ color: 'var(--muted-foreground)' }} />
              <span className="text-xs" style={{ color: 'var(--muted-foreground)' }}>
                No components in manifest.
              </span>
            </div>
          )}
        </div>
      </div>
    </div>
  )
}
