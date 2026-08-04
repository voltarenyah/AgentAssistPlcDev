import {
  Box,
  CircuitBoard,
  ChevronRight,
  Cpu,
  Layers3,
} from 'lucide-react'
import { useEffect, useState } from 'react'
import type { HardwareConfigurationNode, HardwareConfigurationView as HardwareView } from '@/api/client'
import { tagsForHardwareNode } from './hardwareAddressing'

type Props = {
  view: HardwareView | null
  selectedNodeId: string | null
  inspectedNodeId: string | null
  onSelectNode: (node: HardwareConfigurationNode) => void
  onInspectNode: (node: HardwareConfigurationNode) => void
}

const containsNode = (node: HardwareConfigurationNode, id: string): boolean =>
  node.id === id || node.children.some(child => containsNode(child, id))

const typeLabel = (node: HardwareConfigurationNode) =>
  node.typeIdentifier?.split('/').pop() || (node.kind === 'device' ? 'Device' : 'Module')

function EmptyHardware({ message }: { message: string }) {
  return (
    <div className="grid h-full min-h-[520px] place-items-center p-8">
      <div className="max-w-md text-center">
        <div className="mx-auto mb-4 grid h-14 w-14 place-items-center rounded-2xl border bg-card shadow-sm" style={{ borderColor: 'var(--border)' }}>
          <CircuitBoard className="h-7 w-7 text-chart-2" />
        </div>
        <h2 className="text-base font-semibold">Hardware configuration</h2>
        <p className="mt-2 text-[10px] leading-relaxed text-muted-foreground">{message}</p>
      </div>
    </div>
  )
}

export default function HardwareConfigurationView({
  view,
  selectedNodeId,
  inspectedNodeId,
  onSelectNode,
  onInspectNode,
}: Props) {
  const [expandedIds, setExpandedIds] = useState<Set<string>>(() => new Set())

  useEffect(() => {
    setExpandedIds(new Set())
  }, [view?.projectAmlPath])

  if (!view || view.state !== 'available') {
    return <EmptyHardware message={view?.message ?? 'Loading project-level hardware configuration...'} />
  }

  const selectedDevice = view.devices.find(device =>
    selectedNodeId ? containsNode(device, selectedNodeId) : false,
  ) ?? view.devices[0]
  const selectedNode = selectedDevice && selectedNodeId
    ? findNode(selectedDevice, selectedNodeId)
    : selectedDevice
  const activeNode = selectedNode ?? selectedDevice
  const children = activeNode?.children ?? []
  const activeTags = activeNode ? tagsForHardwareNode(activeNode, view.tags) : []
  const inspectedNode = inspectedNodeId ? findHardwareNode(view.devices, inspectedNodeId) : null
  const inspectedTags = inspectedNode ? tagsForHardwareNode(inspectedNode, view.tags) : []

  const toggleExpanded = (node: HardwareConfigurationNode) => {
    if (node.children.length === 0) return
    setExpandedIds(current => {
      const next = new Set(current)
      if (next.has(node.id)) next.delete(node.id)
      else next.add(node.id)
      return next
    })
  }

  return (
    <div className="flex h-full min-h-0 min-w-0 flex-col overflow-hidden">
      <header className="flex shrink-0 items-center gap-3 border-b px-5 py-4" style={{ borderColor: 'var(--border)' }}>
        <div className="grid h-10 w-10 place-items-center rounded-xl bg-chart-2/10">
          <CircuitBoard className="h-5 w-5 text-chart-2" />
        </div>
        <div className="min-w-0">
          <div className="flex items-center gap-2">
            <h1 className="text-sm font-semibold">Hardware configuration</h1>
            <span className="rounded-full bg-emerald-500/10 px-2 py-0.5 text-[8px] font-medium uppercase tracking-[0.12em] text-emerald-600 dark:text-emerald-400">AML loaded</span>
          </div>
          <p className="mt-1 truncate font-mono text-[9px] text-muted-foreground">{view.projectAmlPath}</p>
        </div>
        <div className="ml-auto flex items-center gap-3 text-[9px] text-muted-foreground">
          <span>{view.devices.length} device{view.devices.length === 1 ? '' : 's'}</span>
          {view.exportedAt && <span>{new Date(view.exportedAt).toLocaleString()}</span>}
        </div>
      </header>

      <div className="grid min-h-0 flex-1 grid-cols-[minmax(180px,0.9fr)_minmax(260px,1.4fr)]">
        <section className="flex min-h-0 min-w-0 flex-col overflow-hidden border-r" style={{ borderColor: 'var(--border)' }}>
          <div className="flex items-center gap-2 border-b px-4 py-3" style={{ borderColor: 'var(--border)' }}>
            <Cpu className="h-3.5 w-3.5 text-chart-2" />
            <span className="text-[10px] font-semibold">Project devices</span>
            <span className="ml-auto rounded bg-muted px-1.5 py-0.5 font-mono text-[8px] text-muted-foreground">{view.devices.length}</span>
          </div>
          <div className="scrollbar-sleek min-h-0 flex-1 overflow-y-auto p-2">
            {view.devices.map(device => (
              <HardwareTreeRow
                key={device.id}
                node={device}
                selectedNodeId={selectedNode?.id ?? null}
                onSelectNode={onSelectNode}
                expandedIds={expandedIds}
                onToggleExpanded={toggleExpanded}
              />
            ))}
          </div>
        </section>

        <section className="flex min-h-0 min-w-0 flex-col overflow-hidden">
          <div className="flex items-center gap-2 border-b px-4 py-3" style={{ borderColor: 'var(--border)' }}>
            <Layers3 className="h-3.5 w-3.5 text-chart-3" />
            <span className="text-[10px] font-semibold">Child objects</span>
            {activeNode && <span className="min-w-0 truncate text-[9px] text-muted-foreground">under {activeNode.path}</span>}
            <span className="ml-auto rounded bg-muted px-1.5 py-0.5 font-mono text-[8px] text-muted-foreground">{children.length}</span>
          </div>
          <div className="scrollbar-sleek min-h-0 overflow-y-auto p-3">
            {activeNode && (
              <div className="mb-2 flex w-full items-center gap-2 rounded-lg border border-chart-2/20 bg-chart-2/5 px-3 py-2">
                <CircuitBoard className="h-4 w-4 text-chart-2" />
                <span className="min-w-0 flex-1 truncate text-[10px] font-medium">{activeNode?.name}</span>
                <span className="font-mono text-[8px] text-muted-foreground">inspect children</span>
              </div>
            )}
            {children.length > 0 && children.map(child => (
              <HardwareChildRow key={child.id} node={child} inspectedNodeId={inspectedNodeId} onInspectNode={onInspectNode} />
            ))}
            {children.length === 0 && activeTags.length === 0 && (
              <div className="rounded-lg border border-dashed p-8 text-center" style={{ borderColor: 'var(--border)' }}>
                <Box className="mx-auto mb-2 h-5 w-5 text-muted-foreground" />
                <p className="text-[10px] text-muted-foreground">No child objects or bound tags were exported for this object.</p>
              </div>
            )}
            {activeTags.length > 0 && (
              <HardwareTagSection title={`Bound tags (${activeTags.length})`} tags={activeTags} />
            )}
            {inspectedNode && inspectedNode.id !== activeNode?.id && (
              <div className="mt-3 rounded-xl border bg-muted/10 p-3" style={{ borderColor: 'var(--border)' }}>
                <div className="mb-3 flex items-center gap-2">
                  <InfoDot />
                  <div className="min-w-0">
                    <div className="truncate text-[10px] font-semibold">{inspectedNode.name}</div>
                    <div className="truncate font-mono text-[8px] text-muted-foreground">selected object · {inspectedNode.path}</div>
                  </div>
                </div>
                <HardwareIoRangeList ranges={inspectedNode.ioRanges} />
                {inspectedTags.length > 0 && <HardwareTagSection title={`Bound tags (${inspectedTags.length})`} tags={inspectedTags} />}
              </div>
            )}
          </div>
        </section>
      </div>
    </div>
  )
}

function HardwareTreeRow({
  node,
  selectedNodeId,
  onSelectNode,
  expandedIds,
  onToggleExpanded,
  depth = 0,
}: {
  node: HardwareConfigurationNode
  selectedNodeId: string | null
  onSelectNode: (node: HardwareConfigurationNode) => void
  expandedIds: Set<string>
  onToggleExpanded: (node: HardwareConfigurationNode) => void
  depth?: number
}) {
  const selected = selectedNodeId === node.id
  const isDevice = node.kind === 'device'
  const expanded = expandedIds.has(node.id)
  return (
    <div>
      <button
        aria-label={`Select ${node.path}`}
        aria-expanded={expanded}
        className={`mb-1 flex w-full min-w-0 items-start gap-2 rounded-lg border px-2.5 py-2 text-left transition-colors ${selected ? 'border-chart-2/40 bg-chart-2/10' : 'border-transparent hover:bg-accent/50'}`}
        style={{ paddingLeft: `${10 + depth * 12}px` }}
        onClick={() => {
          onSelectNode(node)
          onToggleExpanded(node)
        }}
      >
        <ChevronRight className={`mt-0.5 h-3.5 w-3.5 shrink-0 text-muted-foreground transition-transform ${expanded ? 'rotate-90' : ''}`} />
        {isDevice ? <Cpu className={`mt-0.5 h-3.5 w-3.5 shrink-0 ${selected ? 'text-chart-2' : 'text-muted-foreground'}`} /> : <Box className={`mt-0.5 h-3.5 w-3.5 shrink-0 ${selected ? 'text-chart-3' : 'text-muted-foreground'}`} />}
        <span className="min-w-0 flex-1">
          <span className="block truncate text-[10px] font-medium">{node.name}</span>
          <span className="mt-0.5 block truncate font-mono text-[8px] text-muted-foreground">{typeLabel(node)}</span>
        </span>
        <span className="shrink-0 rounded bg-muted px-1 py-0.5 text-[8px] text-muted-foreground">{node.children.length}</span>
      </button>
      {expanded && node.children.filter(child => child.children.length > 0).map(child => (
        <HardwareTreeRow
          key={child.id}
          node={child}
          selectedNodeId={selectedNodeId}
          onSelectNode={onSelectNode}
          expandedIds={expandedIds}
          onToggleExpanded={onToggleExpanded}
          depth={depth + 1}
        />
      ))}
    </div>
  )
}

function HardwareChildRow({
  node,
  inspectedNodeId,
  onInspectNode,
}: {
  node: HardwareConfigurationNode
  inspectedNodeId: string | null
  onInspectNode: (node: HardwareConfigurationNode) => void
}) {
  return (
    <div>
      <button
        aria-label={`Inspect ${node.path}`}
        className={`mb-1 flex w-full items-center gap-2 rounded-lg border px-3 py-2.5 text-left ${inspectedNodeId === node.id ? 'border-chart-3/40 bg-chart-3/10' : 'border-transparent hover:bg-accent/50'}`}
        onClick={() => onInspectNode(node)}
      >
        <Box className={`h-4 w-4 ${inspectedNodeId === node.id ? 'text-chart-3' : 'text-muted-foreground'}`} />
        <span className="min-w-0 flex-1 truncate text-[10px] font-medium">{node.name}</span>
        <span className="font-mono text-[8px] text-muted-foreground">{typeLabel(node)}</span>
      </button>
    </div>
  )
}

function HardwareIoRangeList({ ranges }: { ranges: HardwareConfigurationNode['ioRanges'] }) {
  if (ranges.length === 0) return null
  return (
    <div className="mb-3 space-y-1.5">
      <div className="text-[8px] font-semibold uppercase tracking-[0.16em] text-muted-foreground">I/O ranges</div>
      {ranges.map(range => (
        <div key={`${range.ioType}-${range.startAddress}-${range.lengthBits}`} className="grid grid-cols-[auto_1fr] gap-x-3 gap-y-0.5 rounded-lg border bg-card px-2.5 py-2" style={{ borderColor: 'var(--border)' }}>
          <span className={`text-[9px] font-semibold ${range.ioType.toLowerCase() === 'output' ? 'text-amber-600 dark:text-amber-400' : 'text-sky-600 dark:text-sky-400'}`}>{range.ioType}</span>
          <span className="font-mono text-[9px] font-semibold">{range.addressRange}</span>
          <span className="text-[8px] text-muted-foreground">StartAddress</span>
          <span className="font-mono text-[8px] text-muted-foreground">{range.startAddress}</span>
          <span className="text-[8px] text-muted-foreground">Length</span>
          <span className="font-mono text-[8px] text-muted-foreground">{range.lengthBits} bit · {Math.ceil(range.lengthBits / 8)} bytes</span>
        </div>
      ))}
    </div>
  )
}

function HardwareTagSection({ title, tags }: { title: string; tags: HardwareView['tags'] }) {
  return (
    <section className="mt-3 border-t pt-3" style={{ borderColor: 'var(--border)' }}>
      <div className="mb-2 text-[8px] font-semibold uppercase tracking-[0.16em] text-muted-foreground">{title}</div>
      <div className="space-y-1.5">
        {tags.map(tag => (
          <div key={tag.id} className="rounded-lg border bg-card px-2.5 py-2" style={{ borderColor: 'var(--border)' }}>
            <div className="flex items-center gap-2">
              <span className="min-w-0 flex-1 truncate text-[9px] font-medium">{tag.name}</span>
              <span className="font-mono text-[8px] text-muted-foreground">{tag.ioType}</span>
            </div>
            <div className="mt-1 flex flex-wrap gap-x-3 gap-y-0.5 font-mono text-[8px] text-muted-foreground">
              <span>{tag.logicalAddress}</span>
              <span>{tag.dataType}</span>
              {tag.ownerPath && <span className="truncate">{tag.ownerPath}</span>}
            </div>
          </div>
        ))}
      </div>
    </section>
  )
}

function InfoDot() {
  return <span className="grid h-5 w-5 place-items-center rounded-full bg-chart-3/10 text-[9px] font-bold text-chart-3">i</span>
}

function findNode(node: HardwareConfigurationNode, id: string): HardwareConfigurationNode | null {
  if (node.id === id) return node
  for (const child of node.children) {
    const match = findNode(child, id)
    if (match) return match
  }
  return null
}

function findHardwareNode(nodes: HardwareConfigurationNode[], id: string): HardwareConfigurationNode | null {
  for (const node of nodes) {
    const match = findNode(node, id)
    if (match) return match
  }
  return null
}
