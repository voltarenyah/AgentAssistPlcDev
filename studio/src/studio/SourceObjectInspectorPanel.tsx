import { useCallback, useEffect, useState } from 'react'
import { ArrowDown, ArrowUp, ChevronDown, ChevronRight, ExternalLink, FileSearch, Loader2, Network, TableProperties } from 'lucide-react'
import * as api from '@/api/client'
import { ContextMenu, ContextMenuContent, ContextMenuItem, ContextMenuTrigger } from '@/components/ui/context-menu'
import type { SourceInspectorTarget } from '@/studio/workspace/workspaceTypes'

type NetworkCard = { source: api.SourceInspection; network: api.SourceInspectionNetwork; access: string | null }
type Props = {
  workbenchId: string
  worktreeId: string
  deviceId: string
  target: SourceInspectorTarget | null
  referenceTargets: Record<string, string>
  onInspectObject: (relativePath: string) => void
  onInspectUsage: (usage: api.SourceVariableUsage[]) => void
}

export default function SourceObjectInspectorPanel({ workbenchId, worktreeId, deviceId, target, referenceTargets, onInspectObject, onInspectUsage }: Props) {
  const [inspection, setInspection] = useState<api.SourceInspection | null>(null)
  const [cards, setCards] = useState<NetworkCard[]>([])
  const [error, setError] = useState<string | null>(null)
  const isWorkspace = target?.kind === 'usage'

  useEffect(() => {
    let active = true
    setInspection(null); setCards([]); setError(null)
    if (!target) return () => { active = false }
    if (target.kind === 'object') {
      void api.inspectSourceObject(workbenchId, worktreeId, deviceId, target.relativePath)
        .then(value => { if (active) setInspection(value) })
        .catch(reason => { if (active) setError(message(reason)) })
      return () => { active = false }
    }
    const selected = target.usage.filter(item => item.sourceFile && item.networkIndex != null)
    const paths = [...new Set(selected.map(item => item.sourceFile!))]
    if (!paths.length) {
      setError('No inspectable source networks were found. Knowledge results without a source path are not rendered.')
      return () => { active = false }
    }
    void Promise.all(paths.map(path => api.inspectSourceObject(workbenchId, worktreeId, deviceId, path))).then(inspections => {
      if (!active) return
      const byPath = new Map(inspections.map(item => [item.relativePath.replaceAll('\\', '/'), item]))
      const rendered = selected.flatMap(item => {
        const source = byPath.get(item.sourceFile!.replaceAll('\\', '/'))
        const network = source?.networks.find(candidate => candidate.index === item.networkIndex)
        return source && network ? [{ source, network, access: item.access }] : []
      })
      setCards(rendered)
      if (!rendered.length) setError('Knowledge found usage rows, but their XML networks are unavailable in the current source export.')
    }).catch(reason => { if (active) setError(message(reason)) })
    return () => { active = false }
  }, [workbenchId, worktreeId, deviceId, target])

  const showVariableUsage = useCallback((variable: string) => {
    void api.getSourceVariableUsage(workbenchId, worktreeId, deviceId, variable).then(result => {
      if (!result.usages.length) {
        setError(`No read/write or mention networks were found for "${variable}".`)
        return
      }
      onInspectUsage(result.usages)
    }).catch(reason => setError(message(reason)))
  }, [workbenchId, worktreeId, deviceId, onInspectUsage])

  const move = (index: number, delta: number) => setCards(current => {
    const targetIndex = index + delta
    if (targetIndex < 0 || targetIndex >= current.length) return current
    const next = [...current]
    ;[next[index], next[targetIndex]] = [next[targetIndex], next[index]]
    return next
  })
  const title = isWorkspace ? 'Read/write network workspace' : inspection?.name ?? 'Source inspector'
  const subtitle = isWorkspace
    ? 'Networks are loaded from current XML; ordering is temporary for this workspace.'
    : target?.kind === 'object' ? target.relativePath : 'Choose Inspect object from PLC source to render its XML semantic content.'

  return <div className="flex h-full min-h-0 flex-col bg-background">
    <header className="flex shrink-0 items-start gap-3 border-b bg-card px-5 py-4" style={{ borderColor: 'var(--border)' }}>
      <div className="grid h-8 w-8 place-items-center rounded-lg border bg-chart-3/10" style={{ borderColor: 'var(--border)' }}><FileSearch className="h-4 w-4 text-chart-3" /></div>
      <div className="min-w-0 flex-1"><h1 className="truncate text-sm font-semibold">{title}</h1><p className="mt-0.5 break-all font-mono text-[10px] text-muted-foreground">{subtitle}</p></div>
    </header>
    {!target && <EmptyInspector />}
    {target && !inspection && !isWorkspace && !error && <Loading />}
    {target && isWorkspace && !cards.length && !error && <Loading />}
    {error && <div className="m-5 rounded border border-destructive/40 bg-destructive/10 p-3 text-sm text-destructive">{error}</div>}
    {inspection && !isWorkspace && <div className="scrollbar-sleek min-h-0 flex-1 space-y-5 overflow-auto p-5"><InspectorDetails inspection={inspection} onShowUsage={showVariableUsage} />{inspection.networks.map(network => <NetworkCardView key={network.compileUnitId} source={inspection} network={network} access={null} referenceTargets={referenceTargets} onOpenReference={onInspectObject} />)}</div>}
    {isWorkspace && cards.length > 0 && <div className="scrollbar-sleek min-h-0 flex-1 space-y-3 overflow-auto p-5">{cards.map((card, index) => <div key={`${card.source.relativePath}:${card.network.compileUnitId}:${card.access}:${index}`} className="relative"><div className="absolute right-3 top-2 z-10 flex gap-1"><button aria-label="Move network up" disabled={index === 0} onClick={() => move(index, -1)} className="rounded border bg-background p-1 disabled:opacity-40"><ArrowUp className="h-3 w-3" /></button><button aria-label="Move network down" disabled={index === cards.length - 1} onClick={() => move(index, 1)} className="rounded border bg-background p-1 disabled:opacity-40"><ArrowDown className="h-3 w-3" /></button></div><NetworkCardView {...card} referenceTargets={referenceTargets} onOpenReference={onInspectObject} /></div>)}</div>}
  </div>
}

function EmptyInspector() { return <div className="flex min-h-0 flex-1 items-center justify-center p-8"><div className="max-w-md text-center"><FileSearch className="mx-auto h-8 w-8 text-muted-foreground" /><h2 className="mt-3 text-sm font-semibold">No source object selected</h2><p className="mt-1 text-xs leading-relaxed text-muted-foreground">Right-click an item in PLC source and choose <span className="font-medium text-foreground">Inspect object</span>. This view stays dockable while you compare source, knowledge, and chat.</p></div></div> }
function Loading() { return <div className="flex flex-1 items-center justify-center gap-2 text-sm text-muted-foreground"><Loader2 className="h-4 w-4 animate-spin" /> Reading XML…</div> }
function InspectorDetails({ inspection, onShowUsage }: { inspection: api.SourceInspection; onShowUsage: (variable: string) => void }) { return <>{inspection.interfaces.map(section => <section key={section.name}><h2 className="mb-2 text-xs font-semibold">{section.name}</h2><Table columns={['Name', 'Data type', 'Default', 'Access', 'Comment']} rows={section.members.map(member => ({ Name: member.name, 'Data type': member.dataType, Default: member.defaultValue, Access: member.accessibility, Comment: member.comment }))} /></section>)}{inspection.tables.map(table => <section key={table.title}><h2 className="mb-2 flex items-center gap-2 text-xs font-semibold"><TableProperties className="h-3.5 w-3.5" />{table.title}</h2><Table columns={table.columns} rows={table.rows} onShowUsage={inspection.category === 'Tags' ? onShowUsage : undefined} /></section>)}</> }
function NetworkCardView({ source, network, access, referenceTargets, onOpenReference }: NetworkCard & { referenceTargets: Record<string, string>; onOpenReference: (relativePath: string) => void }) {
  const [expanded, setExpanded] = useState(true)
  return <section className="rounded-md border bg-muted/20"><header className="flex items-center gap-1.5 border-b px-2 py-1.5 text-xs font-semibold"><Network className="h-3.5 w-3.5" />{source.name} · Network {network.index}{access && <span className={`rounded px-1.5 py-0.5 text-[10px] ${access === 'write' ? 'bg-amber-500/15 text-amber-700' : access === 'read' ? 'bg-sky-500/15 text-sky-700' : 'bg-muted text-muted-foreground'}`}>{access}</span>}<span className="ml-auto mr-14 rounded bg-background px-1.5 py-0.5 text-[10px]">{network.language ?? 'unknown'}</span><button type="button" aria-label={expanded ? 'Collapse network' : 'Expand network'} aria-expanded={expanded} onClick={() => setExpanded(value => !value)} className="rounded p-0.5 text-muted-foreground hover:bg-accent hover:text-foreground">{expanded ? <ChevronDown className="h-3.5 w-3.5" /> : <ChevronRight className="h-3.5 w-3.5" />}</button></header>{expanded && <>{network.title && <p className="px-2 pt-1 text-xs text-muted-foreground">{network.title}</p>}{network.comment && <p className="px-2 pt-1 text-xs text-muted-foreground">{network.comment}</p>}{network.structuredText && <pre className="m-2 overflow-auto rounded bg-background p-2 text-xs"><code>{network.structuredText}</code></pre>}{network.ladder && <Ladder network={network.ladder} referenceTargets={referenceTargets} onOpenReference={onOpenReference} />}</>}</section>
}
type Ladder = NonNullable<api.SourceInspectionNetwork['ladder']>
type LadderPort = { elementId: string; pin: string }
type LadderConnection = { from: LadderPort; to: LadderPort }
type LadderPlacement = { element: api.LadderElementInspection; x: number; y: number; width: number; height: number; pinYs: Record<string, number> }

function Ladder({ network, referenceTargets, onOpenReference }: { network: Ladder; referenceTargets: Record<string, string>; onOpenReference: (relativePath: string) => void }) {
  const diagram = buildLadderDiagram(network)
  const scale = 0.66
  return <div className="m-2 overflow-x-auto rounded border bg-[#fbfbf8] p-2 dark:bg-background">
    <div className="mb-1 text-[10px] text-muted-foreground">LAD topology · {network.wires.length} connection{network.wires.length === 1 ? '' : 's'}</div>
    <svg aria-label="Ladder logic diagram" className="block min-w-[560px]" height={Math.ceil(diagram.height * scale)} viewBox={`0 0 ${diagram.width} ${diagram.height}`} width={Math.ceil(diagram.width * scale)}>
      <line x1="42" x2="42" y1="24" y2={diagram.height - 24} stroke="currentColor" strokeWidth="3" />
      {diagram.powerInputs.map(port => { const point = diagram.point(port); return <line key={`${port.elementId}:${port.pin}`} x1="42" x2={point.x} y1={point.y} y2={point.y} stroke="currentColor" strokeWidth="2" /> })}
      {diagram.connections.map((connection, index) => <path data-lad-wire="true" key={index} d={route(diagram.point(connection.from), diagram.point(connection.to))} fill="none" stroke="currentColor" strokeWidth="2" />)}
      {diagram.outputStubs.map(port => { const point = diagram.point(port); return <line data-lad-wire="true" key={`${port.elementId}:${port.pin}`} x1={point.x} x2={point.x + 38} y1={point.y} y2={point.y} stroke="currentColor" strokeWidth="2" /> })}
      {diagram.placements.map(placement => <LadderElement key={placement.element.id} placement={placement} referenceTargets={referenceTargets} onOpenReference={onOpenReference} />)}
    </svg>
  </div>
}

function buildLadderDiagram(network: Ladder) {
  const elements = new Map(network.elements.map(element => [element.id, element]))
  const connections: LadderConnection[] = []
  const powerInputs: LadderPort[] = []
  const openOutputs: LadderPort[] = []
  for (const wire of network.wires) {
    const ports = wire.endpoints.filter((endpoint): endpoint is { kind: string; elementId: string; pin: string } => endpoint.kind === 'NameCon' && !!endpoint.elementId && !!endpoint.pin && elements.has(endpoint.elementId)).map(endpoint => ({ elementId: endpoint.elementId, pin: endpoint.pin }))
    const hasPowerrail = wire.endpoints.some(endpoint => endpoint.kind === 'Powerrail')
    if (hasPowerrail) powerInputs.push(...ports)
    if (wire.endpoints.some(endpoint => endpoint.kind === 'OpenCon')) openOutputs.push(...ports)
    if (!hasPowerrail && ports.length === 2) {
      const [first, second] = ports
      connections.push(isOutputPin(first.pin) || !isOutputPin(second.pin) ? { from: first, to: second } : { from: second, to: first })
    }
  }
  const distance = new Map<string, number>()
  const row = new Map<string, number>()
  ;[...new Set(powerInputs.map(port => port.elementId))].forEach((id, index) => { distance.set(id, 0); row.set(id, index) })
  for (let pass = 0; pass < elements.size; pass++) {
    for (const connection of connections) {
      const fromDistance = distance.get(connection.from.elementId)
      if (fromDistance == null) continue
      const nextDistance = fromDistance + 1
      if ((distance.get(connection.to.elementId) ?? -1) < nextDistance) distance.set(connection.to.elementId, nextDistance)
      const sourceRow = row.get(connection.from.elementId) ?? 0
      if (!row.has(connection.to.elementId) || sourceRow < row.get(connection.to.elementId)!) row.set(connection.to.elementId, sourceRow)
    }
  }
  const pinRows = new Map<string, number>()
  for (const connection of connections) {
    const sourceRow = row.get(connection.from.elementId)
    if (sourceRow != null) pinRows.set(`${connection.to.elementId}:${connection.to.pin.toLowerCase()}`, sourceRow)
  }
  const baseline = (index: number) => 112 + index * 220
  let disconnected = 0
  const placements = network.elements.map(element => {
    const block = !/^(contact|coil)$/i.test(element.kind)
    const width = block ? 142 : 104
    const x = 108 + (distance.get(element.id) ?? disconnected++) * 175
    const elementRow = row.get(element.id) ?? 0
    const pinYs: Record<string, number> = {}
    for (const pin of displayPins(element)) {
      const inputRow = pinRows.get(`${element.id}:${pin.name.toLowerCase()}`)
      if (inputRow != null) pinYs[pin.name.toLowerCase()] = baseline(inputRow)
    }
    if (!block) return { element, x, y: baseline(elementRow) - 26, width, height: 52, pinYs }
    if (/^sr$/i.test(element.kind)) {
      const inputRows = ['s', 'r1'].map(pin => pinRows.get(`${element.id}:${pin}`)).filter((value): value is number => value != null)
      const topRow = inputRows.length ? Math.min(...inputRows) : elementRow
      const bottomRow = inputRows.length ? Math.max(...inputRows) : elementRow
      const y = baseline(topRow) - 44
      return { element, x, y, width, height: baseline(bottomRow) - y + 18, pinYs }
    }
    const y = baseline(elementRow) - 46
    pinYs.in = pinYs.in ?? baseline(elementRow)
    pinYs.q = pinYs.q ?? baseline(elementRow)
    return { element, x, y, width, height: 154, pinYs }
  })
  const byId = new Map(placements.map(placement => [placement.element.id, placement]))
  const point = ({ elementId, pin }: LadderPort) => portPoint(byId.get(elementId)!, pin)
  const outputStubs = [...openOutputs]
  for (const element of network.elements) for (const pin of displayPins(element)) {
    if (!isOutputPin(pin.name) || connections.some(connection => connection.from.elementId === element.id && connection.from.pin.toLowerCase() === pin.name.toLowerCase()) || outputStubs.some(port => port.elementId === element.id && port.pin.toLowerCase() === pin.name.toLowerCase())) continue
    outputStubs.push({ elementId: element.id, pin: pin.name })
  }
  const width = Math.max(780, ...placements.map(placement => placement.x + placement.width + 78))
  const height = Math.max(220, ...placements.map(placement => placement.y + placement.height + 42))
  return { placements, connections, powerInputs, outputStubs, point, width, height }
}

function isOutputPin(pin: string) { return /^(out|q|eno|et)$/i.test(pin) }
function route(from: { x: number; y: number }, to: { x: number; y: number }) { const mid = Math.round((from.x + to.x) / 2); return `M ${from.x} ${from.y} H ${mid} V ${to.y} H ${to.x}` }
function portPoint(placement: LadderPlacement, pin: string) {
  const name = pin.toLowerCase()
  const block = !/^(contact|coil)$/i.test(placement.element.kind)
  if (!block) return { x: isOutputPin(name) ? placement.x + placement.width : placement.x, y: placement.y + placement.height / 2 }
  const input = placement.pinYs[name] ?? (name === 'pt' || name === 'r1' ? placement.y + placement.height - 18 : placement.y + 46)
  const output = placement.pinYs[name] ?? (name === 'et' ? placement.y + 96 : placement.y + 46)
  return { x: isOutputPin(name) ? placement.x + placement.width : placement.x, y: isOutputPin(name) ? output : input }
}
function displayPins(element: api.LadderElementInspection) {
  const required = /^ton$/i.test(element.kind) ? ['IN', 'PT', 'Q', 'ET'] : /^sr$/i.test(element.kind) ? ['s', 'r1', 'Q'] : []
  return [...element.pins, ...required.filter(name => !element.pins.some(pin => pin.name.toLowerCase() === name.toLowerCase())).map(name => ({ name, label: null, referencedObject: null, negated: false }))]
}

function LadderElement({ placement, referenceTargets, onOpenReference }: { placement: LadderPlacement; referenceTargets: Record<string, string>; onOpenReference: (relativePath: string) => void }) {
  const { element, x, y, width, height } = placement
  const isContact = /^contact$/i.test(element.kind)
  const reference = element.referencedObject && referenceTargets[element.referencedObject]
  const label = element.label ?? element.id
  const pins = displayPins(element).filter(pin => !/^operand$/i.test(pin.name))
  return <ContextMenu><ContextMenuTrigger asChild><g data-lad-element-id={element.id} data-lad-x={x} data-lad-y={y} data-lad-height={height} role="button" tabIndex={0} aria-label={`${element.kind} ${label}`} className="cursor-context-menu outline-none">
    <rect x={x - 5} y={y - 34} width={width + 10} height={height + 42} fill="transparent" />
    <LadderLabel label={label} x={x + width / 2} y={y - 19} />
    {isContact ? <ContactSymbol x={x} y={y} width={width} height={height} negated={element.negatedPins.some(pin => /^operand$/i.test(pin))} /> : <FunctionBlock placement={placement} pins={pins} />}
  </g></ContextMenuTrigger><ContextMenuContent><ContextMenuItem disabled={!reference} onSelect={() => { if (reference) onOpenReference(reference) }}><ExternalLink className="h-3.5 w-3.5" />Open referenced object</ContextMenuItem></ContextMenuContent></ContextMenu>
}

function LadderLabel({ label, x, y }: { label: string; x: number; y: number }) { const lines = wrapLadderText(label); return <text x={x} y={y - (lines.length - 1) * 10} fill="currentColor" fontSize="11" textAnchor="middle">{lines.map((line, index) => <tspan key={`${line}:${index}`} x={x} dy={index === 0 ? 0 : 10}>{line}</tspan>)}</text> }
function wrapLadderText(value: string) { const words = value.match(/[A-Z]+(?=[A-Z][a-z]|$)|[A-Z]?[a-z]+|\d+|[^A-Za-z0-9]+/g) ?? [value]; const lines: string[] = []; let line = ''; for (const word of words) { if (line && line.length + word.length > 15) { lines.push(line); line = word } else line += word } if (line) lines.push(line); return lines }

function ContactSymbol({ x, y, width, height, negated }: { x: number; y: number; width: number; height: number; negated: boolean }) { const middle = y + height / 2; return <g><line x1={x} x2={x + 30} y1={middle} y2={middle} stroke="currentColor" strokeWidth="2" /><line x1={x + width - 30} x2={x + width} y1={middle} y2={middle} stroke="currentColor" strokeWidth="2" /><line x1={x + 30} x2={x + 30} y1={middle - 14} y2={middle + 14} stroke="currentColor" strokeWidth="2" /><line x1={x + width - 30} x2={x + width - 30} y1={middle - 14} y2={middle + 14} stroke="currentColor" strokeWidth="2" />{negated && <line x1={x + 25} x2={x + width - 25} y1={middle + 18} y2={middle - 18} stroke="currentColor" strokeWidth="2" />}</g> }
function FunctionBlock({ placement, pins }: { placement: LadderPlacement; pins: api.LadderPinInspection[] }) { const { x, y, width, height, element } = placement; return <g><rect x={x} y={y} width={width} height={height} fill="#d9dbe3" stroke="currentColor" strokeWidth="1.5" /><text x={x + width / 2} y={y + 23} fill="currentColor" fontSize="13" fontWeight="700" textAnchor="middle">{element.kind}</text>{pins.map(pin => { const point = portPoint(placement, pin.name); const output = isOutputPin(pin.name); return <g key={pin.name}><text x={output ? point.x - 8 : point.x + 8} y={point.y + 4} fill="currentColor" fontSize="10" textAnchor={output ? 'end' : 'start'}>{pin.name}</text>{pin.label && <text x={output ? point.x - 8 : point.x + 8} y={point.y + 18} fill="#0f766e" fontSize="9" textAnchor={output ? 'end' : 'start'}>{pin.label}</text>}</g> })}</g> }
function Table({ columns, rows, onShowUsage }: { columns: string[]; rows: Record<string, string | null>[]; onShowUsage?: (variable: string) => void }) { return <div className="overflow-auto rounded border"><table className="w-full text-left text-xs"><thead className="bg-muted/50"><tr>{columns.map(column => <th key={column} className="whitespace-nowrap px-3 py-2 font-medium">{column}</th>)}</tr></thead><tbody>{rows.map((row, index) => <ContextMenu key={index}><ContextMenuTrigger asChild><tr className="border-t">{columns.map(column => <td key={column} className="whitespace-nowrap px-3 py-2">{row[column] || '—'}</td>)}</tr></ContextMenuTrigger>{onShowUsage && <ContextMenuContent><ContextMenuItem onSelect={() => { if (row.Name) onShowUsage(row.Name) }}><Network className="h-3.5 w-3.5" />Show read/write networks</ContextMenuItem></ContextMenuContent>}</ContextMenu>)}</tbody></table></div> }
function message(reason: unknown) { return reason instanceof Error ? reason.message : String(reason) }
