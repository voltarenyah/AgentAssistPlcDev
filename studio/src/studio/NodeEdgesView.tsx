import { useState, useEffect, useRef, useCallback } from 'react'
import { Database, Loader2, RefreshCw, Search, Cpu, Blocks } from 'lucide-react'
import * as api from '@/api/client'

/* ── Props ────────────────────────────────────────────── */

interface NodeEdgesViewProps {
  context: api.KnowledgeGraphContext
  projectName: string
  onNodeSelect?: (node: api.GraphNode | null) => void
  onEdgeSelect?: (edge: api.GraphEdge | null) => void
}

const KNOWLEDGE_PAGE_SIZE = 100
const MAX_RENDERED_KNOWLEDGE_ROWS = 200

/* ── Component ────────────────────────────────────────── */

export default function NodeEdgesView({ context, projectName, onNodeSelect, onEdgeSelect }: NodeEdgesViewProps) {
  /* ── DB existence check ───────────────────────────── */
  const [dbExists, setDbExists] = useState(true)
  const [initialCheckDone, setInitialCheckDone] = useState(false)
  const [checkError, setCheckError] = useState<string | null>(null)

  /* ── Node state ───────────────────────────────────── */
  const [nodes, setNodes] = useState<api.GraphNode[]>([])
  const [nodeKinds, setNodeKinds] = useState<string[]>([])
  const [selectedKind, setSelectedKind] = useState<string>('')
  const [nodeSearch, setNodeSearch] = useState('')
  const [debouncedNodeSearch, setDebouncedNodeSearch] = useState('')
  const [selectedNode, setSelectedNode] = useState<api.GraphNode | null>(null)
  const [nodesLoading, setNodesLoading] = useState(true)
  const [nodesLoadingMore, setNodesLoadingMore] = useState(false)
  const [nodesError, setNodesError] = useState<string | null>(null)
  const [nodesTruncated, setNodesTruncated] = useState(false)
  const [nodesTotalCount, setNodesTotalCount] = useState<number | undefined>(undefined)
  const nodesRequestRef = useRef(0)

  /* ── Edge state ───────────────────────────────────── */
  const [edges, setEdges] = useState<api.GraphEdge[]>([])
  const [edgeTypes, setEdgeTypes] = useState<string[]>([])
  const [selectedEdgeType, setSelectedEdgeType] = useState<string>('')
  const [edgeSearch, setEdgeSearch] = useState('')
  const [debouncedEdgeSearch, setDebouncedEdgeSearch] = useState('')
  const [selectedEdge, setSelectedEdge] = useState<api.GraphEdge | null>(null)
  const [edgesLoading, setEdgesLoading] = useState(false)
  const [edgesLoadingMore, setEdgesLoadingMore] = useState(false)
  const [edgesError, setEdgesError] = useState<string | null>(null)
  const [edgesTruncated, setEdgesTruncated] = useState(false)
  const [edgesTotalCount, setEdgesTotalCount] = useState<number | undefined>(undefined)
  const edgesRequestRef = useRef(0)

  /* ── Split-panel drag state ───────────────────────── */
  const [splitRatio, setSplitRatio] = useState(50)
  const splitDragRef = useRef(false)
  const splitContainerRef = useRef<HTMLDivElement>(null)

  /* ── Column resize state ─────────────────────────── */
  const [nodeColWidths, setNodeColWidths] = useState<Record<string, number>>({
    id: 140,
    kind: 72,
    name: 240,
  })
  const [edgeColWidths, setEdgeColWidths] = useState<Record<string, number>>({
    id: 120,
    type: 90,
    from_node_id: 160,
    to_node_id: 160,
  })
  const colResizeRef = useRef<{
    column: string
    startX: number
    startWidth: number
    commit: React.Dispatch<React.SetStateAction<Record<string, number>>>
  } | null>(null)

  /* ── Initial DB check ─────────────────────────────── */
  useEffect(() => {
    // A 404 means the knowledge DB has not been built yet; anything else is a real error.
    setCheckError(null)
    api.getKnowledgeNodeKinds(context)
      .then(() => { setDbExists(true); setInitialCheckDone(true) })
      .catch(e => {
        if ((e as { status?: number }).status === 404) {
          setDbExists(false)
        } else {
          setCheckError(e instanceof Error ? e.message : 'Failed to reach the knowledge API')
        }
        setInitialCheckDone(true)
      })
  }, [context])

  /* ── Fetch node kinds on mount ─────────────────────── */
  useEffect(() => {
    setNodesLoading(true)
    setNodesError(null)
    api.getKnowledgeNodeKinds(context)
      .then(data => setNodeKinds(data.kinds))
      .catch(e => setNodesError(e instanceof Error ? e.message : 'Failed to load node kinds'))
      .finally(() => setNodesLoading(false))
  }, [context])

  /* ── Fetch nodes when kind filter or search changes ── */
  const fetchNodes = useCallback(async (offset = 0, append = false) => {
    const requestId = ++nodesRequestRef.current
    if (append) {
      setNodesLoadingMore(true)
    } else {
      setNodesLoading(true)
      setNodesLoadingMore(false)
      setNodesError(null)
    }
    try {
      const data = await api.getKnowledgeNodes(
        context,
        selectedKind || undefined,
        debouncedNodeSearch || undefined,
        KNOWLEDGE_PAGE_SIZE,
        offset > 0 ? offset : undefined)
      if (requestId !== nodesRequestRef.current) return
      setNodes(prev => (append ? [...prev, ...data.nodes] : data.nodes).slice(0, MAX_RENDERED_KNOWLEDGE_ROWS))
      setNodesTruncated((data.truncated ?? false) || (data.totalCount ?? data.nodes.length) > MAX_RENDERED_KNOWLEDGE_ROWS)
      setNodesTotalCount(data.totalCount)
    } catch (e) {
      if (requestId !== nodesRequestRef.current) return
      if (append) {
        setNodesTruncated(false)
      } else {
        setNodesError(e instanceof Error ? e.message : 'Failed to load nodes')
      }
    } finally {
      if (requestId === nodesRequestRef.current) {
        if (append) {
          setNodesLoadingMore(false)
        } else {
          setNodesLoading(false)
        }
      }
    }
  }, [context, selectedKind, debouncedNodeSearch])

  useEffect(() => {
    if (initialCheckDone && dbExists) {
      fetchNodes()
    }
  }, [fetchNodes, initialCheckDone, dbExists])

  /* ── Fetch edge types ──────────────────────────────── */
  useEffect(() => {
    api.getKnowledgeEdgeTypes(context)
      .then(data => setEdgeTypes(data.types))
      .catch(() => { /* non-critical */ })
  }, [context])

  /* ── Fetch edges; a selected node filters to its related edges (from OR to) ── */
  const fetchEdges = useCallback(async (nodeId?: string, offset = 0, append = false) => {
    const requestId = ++edgesRequestRef.current
    if (append) {
      setEdgesLoadingMore(true)
    } else {
      setEdgesLoading(true)
      setEdgesLoadingMore(false)
      setEdgesError(null)
    }
    try {
      const data = await api.getKnowledgeEdges(
        context,
        nodeId,
        selectedEdgeType || undefined,
        debouncedEdgeSearch || undefined,
        KNOWLEDGE_PAGE_SIZE,
        offset > 0 ? offset : undefined)
      if (requestId !== edgesRequestRef.current) return
      setEdges(prev => (append ? [...prev, ...data.edges] : data.edges).slice(0, MAX_RENDERED_KNOWLEDGE_ROWS))
      setEdgesTruncated((data.truncated ?? false) || (data.totalCount ?? data.edges.length) > MAX_RENDERED_KNOWLEDGE_ROWS)
      setEdgesTotalCount(data.totalCount)
    } catch (e) {
      if (requestId !== edgesRequestRef.current) return
      if (append) {
        setEdgesTruncated(false)
      } else {
        setEdgesError(e instanceof Error ? e.message : 'Failed to load edges')
      }
    } finally {
      if (requestId === edgesRequestRef.current) {
        if (append) {
          setEdgesLoadingMore(false)
        } else {
          setEdgesLoading(false)
        }
      }
    }
  }, [context, selectedEdgeType, debouncedEdgeSearch])

  useEffect(() => {
    if (initialCheckDone && dbExists) {
      fetchEdges(selectedNode?.id)
    }
  }, [selectedNode, fetchEdges, initialCheckDone, dbExists])

  /* ── Debounce search boxes before hitting the server ── */
  useEffect(() => {
    const timer = setTimeout(() => setDebouncedNodeSearch(nodeSearch.trim()), 300)
    return () => clearTimeout(timer)
  }, [nodeSearch])

  useEffect(() => {
    const timer = setTimeout(() => setDebouncedEdgeSearch(edgeSearch.trim()), 300)
    return () => clearTimeout(timer)
  }, [edgeSearch])

  /* ── Handlers ──────────────────────────────────────── */

  const handleNodeClick = (node: api.GraphNode) => {
    setSelectedNode(node)
    setSelectedEdge(null)
    onNodeSelect?.(node)
    onEdgeSelect?.(null)
  }

  const handleEdgeClick = (edge: api.GraphEdge) => {
    setSelectedEdge(edge)
    onEdgeSelect?.(edge)
  }

  const handleRefresh = () => {
    fetchNodes()
    fetchEdges(selectedNode?.id)
  }

  /* ── Split-panel drag logic ────────────────────────── */
  useEffect(() => {
    const handleMouseMove = (e: MouseEvent) => {
      if (!splitDragRef.current || !splitContainerRef.current) return
      const rect = splitContainerRef.current.getBoundingClientRect()
      const pct = ((e.clientX - rect.left) / rect.width) * 100
      setSplitRatio(Math.max(20, Math.min(80, pct)))
    }
    const handleMouseUp = () => {
      if (splitDragRef.current) {
        splitDragRef.current = false
        document.body.style.userSelect = ''
        document.body.style.cursor = ''
      }
    }
    const handleMouseDown = (e: MouseEvent) => {
      const target = e.target as HTMLElement
      // only start split-panel drag if the handle itself is clicked (not a column resize handle in a <th>)
      if (target.closest('[class*="cursor-col-resize"]') && !target.closest('th')) {
        splitDragRef.current = true
        document.body.style.userSelect = 'none'
        document.body.style.cursor = 'col-resize'
      }
    }
    window.addEventListener('mousedown', handleMouseDown)
    window.addEventListener('mousemove', handleMouseMove)
    window.addEventListener('mouseup', handleMouseUp)
    return () => {
      window.removeEventListener('mousedown', handleMouseDown)
      window.removeEventListener('mousemove', handleMouseMove)
      window.removeEventListener('mouseup', handleMouseUp)
      document.body.style.userSelect = ''
      document.body.style.cursor = ''
    }
  }, [])

  /* ── Column resize drag logic ──────────────────────── */
  useEffect(() => {
    const handleMouseMove = (e: MouseEvent) => {
      const drag = colResizeRef.current
      if (!drag) return
      const diff = e.clientX - drag.startX
      const newWidth = Math.max(40, drag.startWidth + diff)
      drag.commit(prev => ({ ...prev, [drag.column]: newWidth }))
    }
    const handleMouseUp = () => {
      if (colResizeRef.current) {
        colResizeRef.current = null
        document.body.style.userSelect = ''
        document.body.style.cursor = ''
      }
    }
    window.addEventListener('mousemove', handleMouseMove)
    window.addEventListener('mouseup', handleMouseUp)
    return () => {
      window.removeEventListener('mousemove', handleMouseMove)
      window.removeEventListener('mouseup', handleMouseUp)
      colResizeRef.current = null
    }
  }, [])

  /* ── Loading check (initial) ───────────────────────── */
  if (!initialCheckDone) {
    return (
      <div className="flex flex-1 flex-col items-center justify-center gap-3">
        <Loader2 className="h-6 w-6 animate-spin" style={{ color: 'var(--primary)' }} />
        <span className="text-xs" style={{ color: 'var(--muted-foreground)' }}>
          Loading knowledge graph...
        </span>
      </div>
    )
  }

  /* ── Knowledge API error (not a missing DB) ────────── */
  if (checkError) {
    return (
      <div className="flex flex-1 flex-col items-center justify-center gap-3 px-6">
        <Database className="h-8 w-8" style={{ color: 'var(--destructive)' }} />
        <span className="text-sm font-medium" style={{ color: 'var(--foreground)' }}>
          Failed to load the knowledge graph
        </span>
        <span className="text-xs text-center max-w-md" style={{ color: 'var(--destructive)' }}>
          {checkError}
        </span>
      </div>
    )
  }

  /* ── No knowledge DB ───────────────────────────────── */
  if (!dbExists) {
    return (
      <div className="flex flex-1 flex-col items-center justify-center gap-3 px-6">
        <Database className="h-8 w-8" style={{ color: 'var(--muted-foreground)' }} />
        <span className="text-sm font-medium" style={{ color: 'var(--foreground)' }}>
          Knowledge database not found
        </span>
        <span className="text-xs text-center max-w-md" style={{ color: 'var(--muted-foreground)' }}>
          No PLC knowledge database exists for this project. Run <strong>ingest_source</strong> on the export root first to build the graph database.
        </span>
      </div>
    )
  }

  return (
    <div className="flex flex-1 flex-col overflow-hidden">
      {/* ── Toolbar ──────────────────────────────────────── */}
      <div className="flex h-7 shrink-0 items-center gap-2 border-b px-2.5 text-[10px]"
        style={{ background: 'var(--card)', color: 'var(--muted-foreground)' }}>
        <Cpu className="h-3.5 w-3.5" style={{ color: 'var(--chart-3)' }} />
        <span className="font-medium" style={{ color: 'var(--foreground)' }}>Nodes & Edges</span>
        <span className="text-[9px]" style={{ color: 'var(--muted-foreground)' }}>
          {projectName}
        </span>
        <div className="flex-1" />
        <button
          onClick={handleRefresh}
          className="flex h-6 w-6 cursor-default items-center justify-center rounded hover:bg-accent"
          title="Refresh"
        >
          <RefreshCw className="h-3.5 w-3.5" style={{ color: 'var(--muted-foreground)' }} />
        </button>
      </div>

      {/* ── Split panel container ────────────────────────── */}
      <div ref={splitContainerRef} className="flex flex-1 overflow-hidden">

        {/* ── LEFT: Nodes panel ──────────────────────────── */}
        <div
          className="flex flex-col overflow-hidden"
          style={{ width: `${splitRatio}%`, minWidth: 0, flex: '0 0 auto' }}
        >
          {/* Nodes toolbar */}
          <div className="flex h-8 shrink-0 items-center gap-2 border-b px-2.5 text-[10px]"
            style={{ background: 'var(--card)', borderColor: 'var(--border)' }}>
            <Search className="h-3 w-3" style={{ color: 'var(--muted-foreground)' }} />
            <span className="font-medium" style={{ color: 'var(--foreground)' }}>Nodes</span>
            <span className="text-[9px]" style={{ color: 'var(--muted-foreground)' }}>
              {nodesTotalCount !== undefined ? `${nodes.length} of ${nodesTotalCount}` : nodes.length} node{(nodesTotalCount ?? nodes.length) !== 1 ? 's' : ''}
              {nodesTruncated ? ' (truncated)' : ''}
            </span>
            <div className="flex-1" />
            {/* Search box */}
            <input
              value={nodeSearch}
              onChange={e => setNodeSearch(e.target.value)}
              placeholder="Search nodes..."
              className="w-28 rounded border px-2 py-0.5 text-[9px] outline-none"
              style={{ background: 'var(--input)', color: 'var(--foreground)', borderColor: 'var(--border)' }}
            />
            {/* Kind filter combo */}
            <select
              value={selectedKind}
              onChange={e => { setSelectedKind(e.target.value); setSelectedNode(null); }}
              className="rounded border px-2 py-0.5 text-[9px] outline-none max-w-[140px]"
              style={{ background: 'var(--input)', color: 'var(--foreground)', borderColor: 'var(--border)' }}
            >
              <option value="">All kinds</option>
              {nodeKinds.map(k => <option key={k} value={k}>{k}</option>)}
            </select>
          </div>

          {/* Nodes content */}
          <div className="flex-1 overflow-y-auto scrollbar-sleek" style={{ background: 'var(--background)' }}>
            {nodesLoading ? (
              <div className="flex h-full items-center justify-center gap-2">
                <Loader2 className="h-4 w-4 animate-spin" style={{ color: 'var(--muted-foreground)' }} />
                <span className="text-[10px]" style={{ color: 'var(--muted-foreground)' }}>Loading nodes...</span>
              </div>
            ) : nodesError ? (
              <div className="flex h-full flex-col items-center justify-center gap-2 px-4">
                <span className="text-[10px]" style={{ color: 'var(--destructive)' }}>{nodesError}</span>
                <button
                  onClick={() => fetchNodes()}
                  className="rounded px-3 py-1 text-[9px] font-medium hover:bg-accent"
                  style={{ background: 'var(--card)', color: 'var(--foreground)' }}
                >
                  Retry
                </button>
              </div>
            ) : nodes.length === 0 ? (
              <div className="flex h-full items-center justify-center">
                <span className="text-[10px]" style={{ color: 'var(--muted-foreground)' }}>
                  {debouncedNodeSearch
                    ? `No nodes matching "${debouncedNodeSearch}"`
                    : selectedKind ? `No nodes of kind "${selectedKind}"` : 'No nodes found'}
                </span>
              </div>
            ) : (
              <>
                <table className="w-full text-[10px] border-collapse table-fixed">
                  <thead>
                    <tr className="sticky top-0" style={{ background: 'var(--card)' }}>
                      <th className="px-2 py-1.5 text-left font-medium text-[9px] relative select-none" style={{ color: 'var(--muted-foreground)', borderBottom: '1px solid var(--border)', width: nodeColWidths.id, minWidth: 40 }}>
                        ID
                        <div onMouseDown={e => { e.preventDefault(); e.stopPropagation(); colResizeRef.current = { column: 'id', startX: e.clientX, startWidth: nodeColWidths.id, commit: setNodeColWidths }; document.body.style.userSelect = 'none'; document.body.style.cursor = 'col-resize' }} className="absolute right-0 top-0 bottom-0 w-2 cursor-col-resize hover:bg-primary/30 active:bg-primary/50 z-10 rounded-sm" />
                      </th>
                      <th className="px-2 py-1.5 text-left font-medium text-[9px] relative select-none" style={{ color: 'var(--muted-foreground)', borderBottom: '1px solid var(--border)', width: nodeColWidths.kind, minWidth: 40 }}>
                        Kind
                        <div onMouseDown={e => { e.preventDefault(); e.stopPropagation(); colResizeRef.current = { column: 'kind', startX: e.clientX, startWidth: nodeColWidths.kind, commit: setNodeColWidths }; document.body.style.userSelect = 'none'; document.body.style.cursor = 'col-resize' }} className="absolute right-0 top-0 bottom-0 w-2 cursor-col-resize hover:bg-primary/30 active:bg-primary/50 z-10 rounded-sm" />
                      </th>
                      <th className="px-2 py-1.5 text-left font-medium text-[9px] relative select-none" style={{ color: 'var(--muted-foreground)', borderBottom: '1px solid var(--border)', width: nodeColWidths.name, minWidth: 40 }}>
                        Name
                        <div onMouseDown={e => { e.preventDefault(); e.stopPropagation(); colResizeRef.current = { column: 'name', startX: e.clientX, startWidth: nodeColWidths.name, commit: setNodeColWidths }; document.body.style.userSelect = 'none'; document.body.style.cursor = 'col-resize' }} className="absolute right-0 top-0 bottom-0 w-2 cursor-col-resize hover:bg-primary/30 active:bg-primary/50 z-10 rounded-sm" />
                      </th>
                    </tr>
                  </thead>
                  <tbody>
                    {nodes.map(node => (
                    <tr
                      key={node.id}
                      onClick={() => handleNodeClick(node)}
                      className="cursor-default hover:bg-accent/50"
                      style={{
                        background: selectedNode?.id === node.id ? 'var(--accent)' : 'transparent',
                        color: selectedNode?.id === node.id ? 'var(--accent-foreground)' : 'var(--foreground)',
                      }}
                    >
                      <td className="px-2 py-1 truncate font-mono" style={{ color: 'var(--muted-foreground)', width: nodeColWidths.id, maxWidth: nodeColWidths.id }}>{node.id}</td>
                      <td className="px-2 py-1 whitespace-nowrap" style={{ width: nodeColWidths.kind, maxWidth: nodeColWidths.kind }}>
                        <span className="rounded px-1 text-[8px] font-medium"
                          style={{ background: `${kindColor(node.kind)}22`, color: kindColor(node.kind) }}>
                          {node.kind}
                        </span>
                      </td>
                      <td className="px-2 py-1 truncate" style={{ width: nodeColWidths.name, maxWidth: nodeColWidths.name }}>{node.name}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
                {nodesTruncated && nodes.length >= MAX_RENDERED_KNOWLEDGE_ROWS ? (
                  <div className="px-3 py-2 text-center text-[9px]" style={{ borderTop: '1px solid var(--border)', color: 'var(--muted-foreground)' }}>
                    Showing the first {MAX_RENDERED_KNOWLEDGE_ROWS} nodes. Use search or filters to view more.
                  </div>
                ) : nodesTruncated && (
                  <div className="flex justify-center py-1.5" style={{ borderTop: '1px solid var(--border)' }}>
                    <button
                      onClick={() => fetchNodes(nodes.length, true)}
                      disabled={nodesLoadingMore}
                      className="rounded px-3 py-1 text-[9px] font-medium hover:bg-accent"
                      style={{ background: 'var(--card)', color: 'var(--foreground)' }}
                    >
                      {nodesLoadingMore ? 'Loading...' : 'Load more'}
                    </button>
                  </div>
                )}
              </>
            )}
          </div>
        </div>

        {/* ── Drag handle ────────────────────────────────── */}
        <div
          className="shrink-0 cursor-col-resize relative z-10 flex items-center justify-center hover:bg-primary/20 active:bg-primary/30"
          style={{ width: 9, background: 'transparent', minWidth: 9 }}
        >
          <div style={{ width: 1, height: '40%', background: 'var(--border)', borderRadius: 1, pointerEvents: 'none' }} />
        </div>

        {/* ── RIGHT: Edges panel ─────────────────────────── */}
        <div
          className="flex flex-col overflow-hidden"
          style={{ width: `${100 - splitRatio}%`, minWidth: 0, flex: '0 0 auto' }}
        >
          {/* Edges toolbar */}
          <div className="flex h-8 shrink-0 items-center gap-2 border-b px-2.5 text-[10px]"
            style={{ background: 'var(--card)', borderColor: 'var(--border)' }}>
            <Blocks className="h-3 w-3" style={{ color: 'var(--muted-foreground)' }} />
            <span className="font-medium" style={{ color: 'var(--foreground)' }}>Edges</span>
            <span className="text-[9px]" style={{ color: 'var(--muted-foreground)' }}>
              {edgesTotalCount !== undefined ? `${edges.length} of ${edgesTotalCount}` : edges.length} edge{(edgesTotalCount ?? edges.length) !== 1 ? 's' : ''}
              {edgesTruncated ? ' (truncated)' : ''}
              {selectedNode ? ` · related to ${selectedNode.name}` : ''}
            </span>
            <div className="flex-1" />
            {/* Search box */}
            <input
              value={edgeSearch}
              onChange={e => setEdgeSearch(e.target.value)}
              placeholder="Search edges..."
              className="w-28 rounded border px-2 py-0.5 text-[9px] outline-none"
              style={{ background: 'var(--input)', color: 'var(--foreground)', borderColor: 'var(--border)' }}
            />
            {/* Type filter combo */}
            <select
              value={selectedEdgeType}
              onChange={e => setSelectedEdgeType(e.target.value)}
              className="rounded border px-2 py-0.5 text-[9px] outline-none max-w-[140px]"
              style={{ background: 'var(--input)', color: 'var(--foreground)', borderColor: 'var(--border)' }}
            >
              <option value="">All types</option>
              {edgeTypes.map(t => <option key={t} value={t}>{t}</option>)}
            </select>
          </div>

          {/* Edges content */}
          <div className="flex-1 overflow-y-auto scrollbar-sleek" style={{ background: 'var(--background)' }}>
            {edgesLoading ? (
              <div className="flex h-full items-center justify-center gap-2">
                <Loader2 className="h-4 w-4 animate-spin" style={{ color: 'var(--muted-foreground)' }} />
                <span className="text-[10px]" style={{ color: 'var(--muted-foreground)' }}>Loading edges...</span>
              </div>
            ) : edgesError ? (
              <div className="flex h-full flex-col items-center justify-center gap-2 px-4">
                <span className="text-[10px]" style={{ color: 'var(--destructive)' }}>{edgesError}</span>
                <button
                  onClick={() => fetchEdges(selectedNode?.id)}
                  className="rounded px-3 py-1 text-[9px] font-medium hover:bg-accent"
                  style={{ background: 'var(--card)', color: 'var(--foreground)' }}
                >
                  Retry
                </button>
              </div>
            ) : edges.length === 0 ? (
              <div className="flex h-full items-center justify-center">
                <span className="text-[10px]" style={{ color: 'var(--muted-foreground)' }}>
                  {debouncedEdgeSearch
                    ? `No edges matching "${debouncedEdgeSearch}"`
                    : selectedNode
                      ? 'No edges related to this node'
                      : selectedEdgeType
                        ? `No edges of type "${selectedEdgeType}"`
                        : 'No edges found'}
                </span>
              </div>
            ) : (
              <>
                <table className="w-full text-[10px] border-collapse table-fixed">
                <thead>
                  <tr className="sticky top-0" style={{ background: 'var(--card)' }}>
                    <th className="px-2 py-1.5 text-left font-medium text-[9px] relative select-none" style={{ color: 'var(--muted-foreground)', borderBottom: '1px solid var(--border)', width: edgeColWidths.id, minWidth: 40 }}>
                      ID
                      <div onMouseDown={e => { e.preventDefault(); e.stopPropagation(); colResizeRef.current = { column: 'id', startX: e.clientX, startWidth: edgeColWidths.id, commit: setEdgeColWidths }; document.body.style.userSelect = 'none'; document.body.style.cursor = 'col-resize' }} className="absolute right-0 top-0 bottom-0 w-2 cursor-col-resize hover:bg-primary/30 active:bg-primary/50 z-10 rounded-sm" />
                    </th>
                    <th className="px-2 py-1.5 text-left font-medium text-[9px] relative select-none" style={{ color: 'var(--muted-foreground)', borderBottom: '1px solid var(--border)', width: edgeColWidths.type, minWidth: 40 }}>
                      Type
                      <div onMouseDown={e => { e.preventDefault(); e.stopPropagation(); colResizeRef.current = { column: 'type', startX: e.clientX, startWidth: edgeColWidths.type, commit: setEdgeColWidths }; document.body.style.userSelect = 'none'; document.body.style.cursor = 'col-resize' }} className="absolute right-0 top-0 bottom-0 w-2 cursor-col-resize hover:bg-primary/30 active:bg-primary/50 z-10 rounded-sm" />
                    </th>
                    <th className="px-2 py-1.5 text-left font-medium text-[9px] relative select-none" style={{ color: 'var(--muted-foreground)', borderBottom: '1px solid var(--border)', width: edgeColWidths.from_node_id, minWidth: 40 }}>
                      From Node ID
                      <div onMouseDown={e => { e.preventDefault(); e.stopPropagation(); colResizeRef.current = { column: 'from_node_id', startX: e.clientX, startWidth: edgeColWidths.from_node_id, commit: setEdgeColWidths }; document.body.style.userSelect = 'none'; document.body.style.cursor = 'col-resize' }} className="absolute right-0 top-0 bottom-0 w-2 cursor-col-resize hover:bg-primary/30 active:bg-primary/50 z-10 rounded-sm" />
                    </th>
                    <th className="px-2 py-1.5 text-left font-medium text-[9px] relative select-none" style={{ color: 'var(--muted-foreground)', borderBottom: '1px solid var(--border)', width: edgeColWidths.to_node_id, minWidth: 40 }}>
                      To Node ID
                      <div onMouseDown={e => { e.preventDefault(); e.stopPropagation(); colResizeRef.current = { column: 'to_node_id', startX: e.clientX, startWidth: edgeColWidths.to_node_id, commit: setEdgeColWidths }; document.body.style.userSelect = 'none'; document.body.style.cursor = 'col-resize' }} className="absolute right-0 top-0 bottom-0 w-2 cursor-col-resize hover:bg-primary/30 active:bg-primary/50 z-10 rounded-sm" />
                    </th>
                  </tr>
                </thead>
                <tbody>
                  {edges.map(edge => (
                    <tr
                      key={edge.id}
                      onClick={() => handleEdgeClick(edge)}
                      className="cursor-default hover:bg-accent/50"
                      style={{
                        background: selectedEdge?.id === edge.id ? 'var(--accent)' : 'transparent',
                        color: selectedEdge?.id === edge.id ? 'var(--accent-foreground)' : 'var(--foreground)',
                      }}
                    >
                      <td className="px-2 py-1 truncate font-mono" style={{ color: 'var(--muted-foreground)', width: edgeColWidths.id, maxWidth: edgeColWidths.id }}>{edge.id}</td>
                      <td className="px-2 py-1 whitespace-nowrap" style={{ width: edgeColWidths.type, maxWidth: edgeColWidths.type }}>
                        <span className="rounded px-1 text-[8px] font-medium"
                          style={{ background: `${edgeTypeColor(edge.type)}22`, color: edgeTypeColor(edge.type) }}>
                          {edge.type}
                        </span>
                      </td>
                      <td className="px-2 py-1 truncate font-mono" style={{ color: 'var(--muted-foreground)', width: edgeColWidths.from_node_id, maxWidth: edgeColWidths.from_node_id }}>{edge.from_node_id}</td>
                      <td className="px-2 py-1 truncate font-mono" style={{ color: 'var(--muted-foreground)', width: edgeColWidths.to_node_id, maxWidth: edgeColWidths.to_node_id }}>{edge.to_node_id}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
                {edgesTruncated && edges.length >= MAX_RENDERED_KNOWLEDGE_ROWS ? (
                  <div className="px-3 py-2 text-center text-[9px]" style={{ borderTop: '1px solid var(--border)', color: 'var(--muted-foreground)' }}>
                    Showing the first {MAX_RENDERED_KNOWLEDGE_ROWS} edges. Use search or filters to view more.
                  </div>
                ) : edgesTruncated && (
                  <div className="flex justify-center py-1.5" style={{ borderTop: '1px solid var(--border)' }}>
                    <button
                      onClick={() => fetchEdges(selectedNode?.id, edges.length, true)}
                      disabled={edgesLoadingMore}
                      className="rounded px-3 py-1 text-[9px] font-medium hover:bg-accent"
                      style={{ background: 'var(--card)', color: 'var(--foreground)' }}
                    >
                      {edgesLoadingMore ? 'Loading...' : 'Load more'}
                    </button>
                  </div>
                )}
              </>
            )}
          </div>
        </div>

      </div>
    </div>
  )
}

/* ── Color helpers ────────────────────────────────────── */

function kindColor(kind: string): string {
  const colors: Record<string, string> = {
    'Project': '#f59e0b',
    'PLC Device': '#10b981',
    'OB': '#ef4444',
    'FB': '#3b82f6',
    'FC': '#8b5cf6',
    'Network': '#06b6d4',
    'Instruction': '#f97316',
    'Variable': '#ec4899',
    'Global DB': '#14b8a6',
    'Instance DB': '#14b8a6',
    'DB Member': '#0ea5e9',
    'UDT': '#a855f7',
    'UDT Member': '#a855f7',
    'Data Type': '#6366f1',
    'PLC Tag': '#22c55e',
    'IO Address': '#eab308',
    'Hardware Device': '#78716c',
  }
  return colors[kind] ?? '#78716c'
}

function edgeTypeColor(type: string): string {
  const colors: Record<string, string> = {
    'CONTAINS': '#14b8a6',
    'CALLS': '#f97316',
    'READS': '#3b82f6',
    'WRITES': '#ef4444',
    'DECLARES': '#a855f7',
    'HAS_TYPE': '#6366f1',
    'INSTANCE_OF': '#06b6d4',
    'CONNECTED_TO': '#22c55e',
    'EXECUTES_BEFORE': '#f59e0b',
    'EXECUTES_AFTER': '#f59e0b',
  }
  return colors[type] ?? '#78716c'
}
