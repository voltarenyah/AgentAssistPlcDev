import { useState, useEffect, useCallback } from 'react'
import { Loader2, RefreshCw, FileText, ChevronRight, ChevronDown, Database, BookOpen } from 'lucide-react'
import * as api from '@/api/client'

/* ── Helpers ──────────────────────────────────────────── */

const langColors: Record<string, string> = {
  SCL: '#4ec9b0',
  LAD: '#569cd6',
  FBD: '#c586c0',
  STL: '#dcdcaa',
  GRAPH: '#ce9178',
}

/* ── Props ────────────────────────────────────────────── */

interface BlockSourceViewProps {
  blockName: string
  plcName?: string
}

/* ── Component ────────────────────────────────────────── */

export default function BlockSourceView({ blockName, plcName }: BlockSourceViewProps) {
  const [result, setResult] = useState<api.BlockSourceResult | null>(null)
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)
  const [expanded, setExpanded] = useState<Set<string>>(new Set())

  const toggleNetwork = (id: string) => {
    setExpanded(prev => {
      const next = new Set(prev)
      if (next.has(id)) next.delete(id)
      else next.add(id)
      return next
    })
  }

  const fetchData = useCallback(async () => {
    if (!blockName) {
      setLoading(false)
      return
    }
    setLoading(true)
    setError(null)
    try {
      const data = await api.getBlockSourceCode(blockName, plcName)
      setResult(data)
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Failed to load block source')
    } finally {
      setLoading(false)
    }
  }, [blockName, plcName])

  useEffect(() => { fetchData() }, [fetchData])

  /* ── No block name ──────────────────────────────────── */
  if (!blockName) {
    return (
      <div className="flex flex-1 flex-col items-center justify-center gap-2 text-center">
        <FileText className="h-8 w-8" style={{ color: 'var(--muted-foreground)' }} />
        <span className="text-sm font-medium" style={{ color: 'var(--foreground)' }}>
          No block selected
        </span>
        <span className="text-xs max-w-sm" style={{ color: 'var(--muted-foreground)' }}>
          Right-click a block and select "Open source code" to view its networks.
        </span>
      </div>
    )
  }

  /* ── Loading ────────────────────────────────────────── */
  if (loading) {
    return (
      <div className="flex flex-1 flex-col items-center justify-center gap-3">
        <Loader2 className="h-6 w-6 animate-spin" style={{ color: 'var(--primary)' }} />
        <span className="text-xs" style={{ color: 'var(--muted-foreground)' }}>
          Loading source code for {blockName}...
        </span>
      </div>
    )
  }

  /* ── Error ──────────────────────────────────────────── */
  if (error) {
    return (
      <div className="flex flex-1 flex-col items-center justify-center gap-3 px-6">
        <div className="rounded-full p-3" style={{ background: 'rgba(239,68,68,0.1)' }}>
          <Loader2 className="h-6 w-6" style={{ color: 'var(--destructive)' }} />
        </div>
        <span className="text-sm font-medium" style={{ color: 'var(--foreground)' }}>
          Failed to load source
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

  /* ── No knowledge DB ────────────────────────────────── */
  if (result && !result.exists) {
    return (
      <div className="flex flex-1 flex-col items-center justify-center gap-3 px-6">
        <Database className="h-8 w-8" style={{ color: 'var(--muted-foreground)' }} />
        <span className="text-sm font-medium" style={{ color: 'var(--foreground)' }}>
          Knowledge database not found
        </span>
        <span className="text-xs text-center max-w-md" style={{ color: 'var(--muted-foreground)' }}>
          {result.message || 'Run ingest_source on the export root to build the knowledge database.'}
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

  const block = result?.block
  const networks = result?.networks ?? []
  const isDataBlock = block?.kind === 'DB'

  /* ── Data block (no source code) ────────────────────── */
  if (isDataBlock) {
    return (
      <div className="flex flex-1 flex-col overflow-hidden">
        <div className="flex h-7 shrink-0 items-center gap-2 border-b px-2.5 text-[10px]"
          style={{ background: 'var(--card)', color: 'var(--muted-foreground)' }}>
          <FileText className="h-3.5 w-3.5" style={{ color: 'var(--chart-4)' }} />
          <span className="font-medium" style={{ color: 'var(--foreground)' }}>{block.name}</span>
          <span className="rounded px-1 text-[8px] font-medium"
            style={{ background: '#a78bfa22', color: '#a78bfa' }}>{block.kind}</span>
          <div className="flex-1" />
        </div>
        <div className="flex flex-1 flex-col items-center justify-center gap-2 text-center px-6">
          <Database className="h-8 w-8" style={{ color: 'var(--muted-foreground)' }} />
          <span className="text-sm font-medium" style={{ color: 'var(--foreground)' }}>
            Data block — no source code
          </span>
          <span className="text-xs max-w-sm" style={{ color: 'var(--muted-foreground)' }}>
            {block.name} is a data block (DB) and does not contain executable networks or source code.
          </span>
        </div>
      </div>
    )
  }

  /* ── Block with no networks ─────────────────────────── */
  if (networks.length === 0) {
    return (
      <div className="flex flex-1 flex-col overflow-hidden">
        <div className="flex h-7 shrink-0 items-center gap-2 border-b px-2.5 text-[10px]"
          style={{ background: 'var(--card)', color: 'var(--muted-foreground)' }}>
          <FileText className="h-3.5 w-3.5" style={{ color: 'var(--chart-4)' }} />
          <span className="font-medium" style={{ color: 'var(--foreground)' }}>{block?.name ?? blockName}</span>
          {block && (
            <span className="rounded px-1 text-[8px] font-medium"
              style={{
                background: `${langColors[block.kind] ?? 'var(--accent)'}22`,
                color: langColors[block.kind] ?? 'var(--muted-foreground)',
              }}>{block.kind}</span>
          )}
          <div className="flex-1" />
        </div>
        <div className="flex flex-1 flex-col items-center justify-center gap-2 text-center px-6">
          <BookOpen className="h-8 w-8" style={{ color: 'var(--muted-foreground)' }} />
          <span className="text-sm font-medium" style={{ color: 'var(--foreground)' }}>
            No networks found
          </span>
          <span className="text-xs max-w-sm" style={{ color: 'var(--muted-foreground)' }}>
            This block has no networks. Try selecting a different block.
          </span>
        </div>
      </div>
    )
  }

  /* ── Data: networks with expandable source ──────────── */
  return (
    <div className="flex flex-1 flex-col overflow-hidden">
      {/* ── Toolbar ──────────────────────────────────── */}
      <div className="flex h-7 shrink-0 items-center gap-2 border-b px-2.5 text-[10px]"
        style={{ background: 'var(--card)', color: 'var(--muted-foreground)' }}>
        <FileText className="h-3.5 w-3.5" style={{ color: 'var(--chart-4)' }} />
        <span className="font-medium" style={{ color: 'var(--foreground)' }}>{block?.name ?? blockName}</span>
        {block && (
          <span className="rounded px-1 text-[8px] font-medium"
            style={{
              background: `${langColors[block.kind] ?? 'var(--accent)'}22`,
              color: langColors[block.kind] ?? 'var(--muted-foreground)',
            }}>{block.kind}</span>
        )}
        <span className="text-[9px]" style={{ color: 'var(--muted-foreground)' }}>
          {networks.length} network{networks.length !== 1 ? 's' : ''}
        </span>
        <div className="flex-1" />
        <button
          onClick={fetchData}
          className="flex h-6 w-6 cursor-default items-center justify-center rounded hover:bg-accent"
          title="Refresh"
        >
          <RefreshCw className="h-3.5 w-3.5" style={{ color: 'var(--muted-foreground)' }} />
        </button>
      </div>

      {/* ── Scrollable network list ───────────────────── */}
      <div className="flex-1 overflow-y-auto scrollbar-sleek">
        <div className="p-3 space-y-2 max-w-4xl mx-auto">
          {networks.map((network) => {
            const netId = network.id || `net-${network.index}`
            const isExpanded = expanded.has(netId)
            return (
              <div key={netId}
                className="rounded-lg border overflow-hidden"
                style={{ background: 'var(--card)', borderColor: 'var(--border)' }}>
                {/* Network header — clickable to toggle */}
                <div
                  onClick={() => toggleNetwork(netId)}
                  className="flex cursor-default items-center gap-2 px-3 py-2 hover:bg-accent/30 text-[10px]"
                >
                  <span style={{ color: 'var(--muted-foreground)' }}>
                    {isExpanded ? <ChevronDown className="h-3.5 w-3.5" /> : <ChevronRight className="h-3.5 w-3.5" />}
                  </span>
                  <span className="font-medium" style={{ color: 'var(--foreground)' }}>
                    Network {network.index ?? '?'}
                  </span>
                  {network.title && network.title !== `Network ${network.index}` && (
                    <span className="truncate max-w-[300px]" style={{ color: 'var(--muted-foreground)' }}>
                      — {network.title}
                    </span>
                  )}
                  <div className="flex-1" />
                  {network.language && (
                    <span className="rounded px-1.5 py-0.5 text-[8px] font-medium"
                      style={{
                        background: `${langColors[network.language] ?? 'var(--accent)'}22`,
                        color: langColors[network.language] ?? 'var(--muted-foreground)',
                      }}>
                      {network.language}
                    </span>
                  )}
                  {network.compileUnitId && (
                    <span className="text-[8px] font-mono" style={{ color: 'var(--muted-foreground)' }}>
                      #{network.compileUnitId}
                    </span>
                  )}
                </div>

                {/* Expanded source code */}
                {isExpanded && network.logicStatements && (
                  <div className="border-t px-3 py-2" style={{ borderColor: 'var(--border)', background: 'var(--background)' }}>
                    <pre
                      className="font-mono text-[10px] leading-relaxed whitespace-pre-wrap select-text"
                      style={{ color: 'var(--foreground)', userSelect: 'text', WebkitUserSelect: 'text' }}
                    >
                      <code>{network.logicStatements}</code>
                    </pre>
                  </div>
                )}

                {/* Expanded — no source */}
                {isExpanded && !network.logicStatements && (
                  <div className="border-t px-3 py-3 text-center text-[9px]"
                    style={{ borderColor: 'var(--border)', color: 'var(--muted-foreground)' }}>
                    No source code available for this network.
                  </div>
                )}
              </div>
            )
          })}
        </div>
      </div>
    </div>
  )
}
