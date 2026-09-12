import { useEffect, useState } from 'react'
import { ArrowDown, ArrowUp, ExternalLink, Loader2, Network, TableProperties } from 'lucide-react'
import * as api from '@/api/client'
import { ContextMenu, ContextMenuContent, ContextMenuItem, ContextMenuTrigger } from '@/components/ui/context-menu'
import { Dialog, DialogContent, DialogDescription, DialogHeader, DialogTitle } from '@/components/ui/dialog'

type NetworkCard = { source: api.SourceInspection; network: api.SourceInspectionNetwork; access: string | null }
type Props = { workbenchId: string; worktreeId: string; deviceId: string; relativePath?: string; usage?: api.SourceVariableUsage[]; referenceTargets: Record<string, string>; onOpenReference: (relativePath: string) => void; onShowUsage: (variable: string) => void; onClose: () => void }

export default function SourceObjectInspectorDialog({ workbenchId, worktreeId, deviceId, relativePath, usage, referenceTargets, onOpenReference, onShowUsage, onClose }: Props) {
  const [inspection, setInspection] = useState<api.SourceInspection | null>(null)
  const [cards, setCards] = useState<NetworkCard[]>([])
  const [error, setError] = useState<string | null>(null)
  const isWorkspace = Boolean(usage)

  useEffect(() => {
    let active = true
    setInspection(null); setCards([]); setError(null)
    if (!usage && relativePath) {
      void api.inspectSourceObject(workbenchId, worktreeId, deviceId, relativePath).then(value => { if (active) setInspection(value) }).catch(reason => { if (active) setError(message(reason)) })
      return () => { active = false }
    }
    const selected = usage?.filter(item => item.sourceFile && item.networkIndex != null) ?? []
    const paths = [...new Set(selected.map(item => item.sourceFile!))]
    if (!paths.length) { setError('No inspectable source networks were found. Knowledge results without a source path are not rendered.'); return () => { active = false } }
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
  }, [workbenchId, worktreeId, deviceId, relativePath, usage])

  const move = (index: number, delta: number) => setCards(current => {
    const target = index + delta
    if (target < 0 || target >= current.length) return current
    const next = [...current]
    ;[next[index], next[target]] = [next[target], next[index]]
    return next
  })
  const title = isWorkspace ? 'Read/write network workspace' : inspection?.name ?? 'Inspecting source object'
  const subtitle = isWorkspace ? 'Networks are loaded from current XML; ordering is temporary for this window.' : relativePath
  return <Dialog open onOpenChange={open => { if (!open) onClose() }}><DialogContent className="flex h-[85vh] max-w-6xl flex-col overflow-hidden">
    <DialogHeader><DialogTitle>{title}</DialogTitle><DialogDescription className="font-mono text-[10px]">{subtitle}</DialogDescription></DialogHeader>
    {!inspection && !isWorkspace && !error && <Loading />}{isWorkspace && !cards.length && !error && <Loading />}
    {error && <div className="rounded border border-destructive/40 bg-destructive/10 p-3 text-sm text-destructive">{error}</div>}
    {inspection && !isWorkspace && <div className="scrollbar-sleek min-h-0 flex-1 space-y-5 overflow-auto pr-2"><InspectorDetails inspection={inspection} onShowUsage={onShowUsage} />{inspection.networks.map(network => <NetworkCardView key={network.compileUnitId} source={inspection} network={network} access={null} referenceTargets={referenceTargets} onOpenReference={onOpenReference} />)}</div>}
    {isWorkspace && cards.length > 0 && <div className="scrollbar-sleek min-h-0 flex-1 space-y-3 overflow-auto pr-2">{cards.map((card, index) => <div key={`${card.source.relativePath}:${card.network.compileUnitId}:${card.access}:${index}`} className="relative"><div className="absolute right-3 top-2 z-10 flex gap-1"><button aria-label="Move network up" disabled={index === 0} onClick={() => move(index, -1)} className="rounded border bg-background p-1 disabled:opacity-40"><ArrowUp className="h-3 w-3" /></button><button aria-label="Move network down" disabled={index === cards.length - 1} onClick={() => move(index, 1)} className="rounded border bg-background p-1 disabled:opacity-40"><ArrowDown className="h-3 w-3" /></button></div><NetworkCardView {...card} referenceTargets={referenceTargets} onOpenReference={onOpenReference} /></div>)}</div>}
  </DialogContent></Dialog>
}

function Loading() { return <div className="flex flex-1 items-center justify-center gap-2 text-sm text-muted-foreground"><Loader2 className="h-4 w-4 animate-spin" /> Reading XML…</div> }
function InspectorDetails({ inspection, onShowUsage }: { inspection: api.SourceInspection; onShowUsage: (variable: string) => void }) { return <>{inspection.interfaces.map(section => <section key={section.name}><h3 className="mb-2 text-xs font-semibold">{section.name}</h3><Table columns={['Name', 'Data type', 'Default', 'Access', 'Comment']} rows={section.members.map(member => ({ Name: member.name, 'Data type': member.dataType, Default: member.defaultValue, Access: member.accessibility, Comment: member.comment }))} /></section>)}{inspection.tables.map(table => <section key={table.title}><h3 className="mb-2 flex items-center gap-2 text-xs font-semibold"><TableProperties className="h-3.5 w-3.5" />{table.title}</h3><Table columns={table.columns} rows={table.rows} onShowUsage={inspection.category === 'Tags' ? onShowUsage : undefined} /></section>)}</> }
function NetworkCardView({ source, network, access, referenceTargets, onOpenReference }: NetworkCard & { referenceTargets: Record<string, string>; onOpenReference: (relativePath: string) => void }) { return <section className="rounded-lg border bg-muted/20"><header className="flex items-center gap-2 border-b px-3 py-2 text-xs font-semibold"><Network className="h-3.5 w-3.5" />{source.name} · Network {network.index}{access && <span className={`rounded px-1.5 py-0.5 text-[10px] ${access === 'write' ? 'bg-amber-500/15 text-amber-700' : access === 'read' ? 'bg-sky-500/15 text-sky-700' : 'bg-muted text-muted-foreground'}`}>{access}</span>}<span className="ml-auto mr-16 rounded bg-background px-1.5 py-0.5 text-[10px]">{network.language ?? 'unknown'}</span></header>{network.title && <p className="px-3 pt-2 text-xs text-muted-foreground">{network.title}</p>}{network.comment && <p className="px-3 pt-1 text-xs text-muted-foreground">{network.comment}</p>}{network.structuredText && <pre className="m-3 overflow-auto rounded bg-background p-3 text-xs"><code>{network.structuredText}</code></pre>}{network.ladder && <Ladder network={network.ladder} referenceTargets={referenceTargets} onOpenReference={onOpenReference} />}</section> }
function Ladder({ network, referenceTargets, onOpenReference }: { network: NonNullable<api.SourceInspectionNetwork['ladder']>; referenceTargets: Record<string, string>; onOpenReference: (relativePath: string) => void }) { return <div className="m-3 overflow-x-auto rounded border bg-background p-4"><div className="mb-2 text-[10px] text-muted-foreground">LAD topology · {network.wires.length} connection{network.wires.length === 1 ? '' : 's'}</div><div className="flex min-h-32 min-w-max items-center"><span className="h-24 border-l-2 border-foreground" />{network.elements.map((element, index) => <div key={element.id} className="flex items-center"><span className="w-5 border-t-2 border-foreground" /><ContextMenu><ContextMenuTrigger asChild><button type="button" className="min-w-28 rounded border border-primary/40 bg-card px-3 py-2 text-left text-xs shadow-sm hover:bg-accent" title={element.referencedObject ?? undefined}><b>{element.negatedPins.length ? '¬ ' : ''}{element.kind}</b><br /><span className="text-muted-foreground">{element.label ?? element.id}</span></button></ContextMenuTrigger><ContextMenuContent><ContextMenuItem disabled={!element.referencedObject || !referenceTargets[element.referencedObject]} onSelect={() => { const target = element.referencedObject && referenceTargets[element.referencedObject]; if (target) onOpenReference(target) }}><ExternalLink className="h-3.5 w-3.5" />Open referenced object</ContextMenuItem></ContextMenuContent></ContextMenu><span className={index === network.elements.length - 1 ? 'w-5 border-t-2 border-foreground' : 'w-8 border-t-2 border-foreground'} /></div>)}<span className="h-24 border-r-2 border-foreground" /></div></div> }
function Table({ columns, rows, onShowUsage }: { columns: string[]; rows: Record<string, string | null>[]; onShowUsage?: (variable: string) => void }) { return <div className="overflow-auto rounded border"><table className="w-full text-left text-xs"><thead className="bg-muted/50"><tr>{columns.map(column => <th key={column} className="whitespace-nowrap px-3 py-2 font-medium">{column}</th>)}</tr></thead><tbody>{rows.map((row, index) => <ContextMenu key={index}><ContextMenuTrigger asChild><tr className="border-t">{columns.map(column => <td key={column} className="whitespace-nowrap px-3 py-2">{row[column] || '—'}</td>)}</tr></ContextMenuTrigger>{onShowUsage && <ContextMenuContent><ContextMenuItem onSelect={() => { if (row.Name) onShowUsage(row.Name) }}><Network className="h-3.5 w-3.5" />Show read/write networks</ContextMenuItem></ContextMenuContent>}</ContextMenu>)}</tbody></table></div> }
function message(reason: unknown) { return reason instanceof Error ? reason.message : String(reason) }
