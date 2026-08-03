import { useEffect, useMemo, useState } from 'react'
import {
  AlertTriangle,
  BookOpen,
  CheckCircle2,
  ChevronDown,
  Code2,
  Database,
  GitBranch,
  RefreshCw,
  Search,
  Server,
  ShieldAlert,
  ShieldCheck,
  Wrench,
  X,
} from 'lucide-react'
import * as api from '@/api/client'

type JsonObject = Record<string, unknown>
type ToolSchema = JsonObject & {
  properties?: JsonObject
  required?: string[]
  additionalProperties?: boolean | JsonObject
}

type ToolGuide = {
  prerequisites: string[]
  constraints: string[]
}

const serverMeta: Record<string, { label: string; icon: typeof Server; tone: string }> = {
  engineering: { label: 'Engineering', icon: Server, tone: 'text-chart-2' },
  knowledge: { label: 'Knowledge', icon: Database, tone: 'text-emerald-500' },
  sourceeditor: { label: 'Source editor', icon: Code2, tone: 'text-chart-3' },
  versioncontrol: { label: 'Version control', icon: GitBranch, tone: 'text-chart-4' },
}

const tierMeta: Record<string, { label: string; className: string; icon: typeof ShieldCheck }> = {
  read: { label: 'Read', className: 'bg-emerald-500/10 text-emerald-600 dark:text-emerald-400', icon: ShieldCheck },
  write: { label: 'Write', className: 'bg-amber-500/10 text-amber-600 dark:text-amber-400', icon: AlertTriangle },
  destructive: { label: 'Destructive', className: 'bg-red-500/10 text-red-600 dark:text-red-400', icon: ShieldAlert },
  denied: { label: 'Denied', className: 'bg-muted text-muted-foreground', icon: ShieldAlert },
  unknown: { label: 'Unclassified', className: 'bg-red-500/10 text-red-600 dark:text-red-400', icon: ShieldAlert },
}

const asString = (value: unknown) => typeof value === 'string' ? value : null

const getGuide = (tool: api.ToolInfo): ToolGuide => {
  const prerequisites: string[] = []
  const constraints: string[] = []

  if (tool.serverName === 'engineering') {
    if (!['check_environment', 'list_sessions', 'connect'].includes(tool.name)) {
      prerequisites.push('A TIA Portal session or project connection must already be available.')
    }
    if (tool.name === 'connect') prerequisites.push('Provide exactly one of sessionId or projectPath.')
    if (['open_block_in_editor'].includes(tool.name)) prerequisites.push('The connected TIA session must have a visible UI.')
    if (['compile_block', 'compile_plc'].includes(tool.name)) prerequisites.push('A selected PLC device is required for multi-PLC projects.')
    if (tool.name.startsWith('export_') || ['sync_export', 'rebuild_export', 'get_context_status', 'compare_context'].includes(tool.name)) {
      prerequisites.push('The output or export root must be inside an allowed sandbox root.')
    }
    if (tool.name === 'import_block') prerequisites.push('Validate the XML and snapshot the working folder before importing.')
    constraints.push('Engineering path arguments are checked by the Openness guard and path jail.')
  }

  if (tool.serverName === 'knowledge') {
    prerequisites.push('The selected device context supplies the knowledge database and source roots in the app.')
    if (tool.name === 'query') prerequisites.push('Call get_schema first in a chat and verify table and column names from its DDL.')
    if (['get_block', 'get_single_network', 'get_all_networks', 'get_variable_usage', 'search'].includes(tool.name)) {
      prerequisites.push('The knowledge database must exist and reflect the current source snapshot.')
    }
    if (tool.name === 'ingest_source') constraints.push('This rebuilds the SQLite database; it does not change TIA project state.')
    if (tool.name === 'update_components') constraints.push('Only selected overlay component paths are replaced transactionally.')
    if (tool.name === 'query') constraints.push('SQL must be a single read-only SELECT, WITH, or EXPLAIN statement.')
  }

  if (tool.serverName === 'sourceeditor') {
    prerequisites.push('Source paths must resolve inside the selected device exported-source or modified-source roots.')
    if (tool.name === 'src_apply_edits') prerequisites.push('Use the relative sourceFile returned by get_block; the app prepares the editable overlay.')
    if (tool.name === 'src_validate' && Object.keys(tool.schema.properties ?? {}).includes('baselineFilePath')) {
      constraints.push('Pass baselineFilePath when protected PLC logic and structure must be proven unchanged.')
    }
    constraints.push('Edits are local XML overlay changes; they are not imported into TIA automatically.')
  }

  if (tool.serverName === 'versioncontrol') {
    prerequisites.push('A selected worktree context must be available.')
    constraints.push('The app binds repoPath to the selected worktree; callers should not substitute another repository path.')
    if (tool.name === 'vc_commit') prerequisites.push('At least one change must already be staged.')
    if (tool.name === 'vc_restore') constraints.push('Restoring discards working-tree changes and needs confirmation.')
  }

  const tier = tool.tier ?? 'unknown'
  if (tier === 'destructive') constraints.push('Requires explicit user confirmation and is subject to the per-session destructive-call budget.')
  if (tier === 'write') constraints.push('Changes project, local files, Git state, or portal state but does not require destructive confirmation.')
  if (tier === 'denied' || tier === 'unknown') constraints.push('Blocked by the sandbox policy until it is explicitly classified.')
  return { prerequisites, constraints }
}

const getTypeLabel = (value: unknown): string => {
  const schema = (value ?? {}) as JsonObject
  if (Array.isArray(schema.enum)) return `enum · ${schema.enum.map(item => JSON.stringify(item)).join(' | ')}`
  if (Array.isArray(schema.type)) return schema.type.join(' | ')
  if (typeof schema.type === 'string') return schema.type
  if (Array.isArray(schema.anyOf)) return schema.anyOf.map(getTypeLabel).join(' | ')
  if (schema.$ref) return String(schema.$ref).split('/').pop() ?? 'object'
  return 'object'
}

const getDefaultLabel = (value: unknown) => value === undefined ? null : typeof value === 'string' ? `“${value}”` : JSON.stringify(value)

function TierBadge({ tier }: { tier: string }) {
  const meta = tierMeta[tier] ?? tierMeta.unknown
  const Icon = meta.icon
  return <span className={`inline-flex items-center gap-1 rounded-full px-2 py-1 text-[9px] font-medium ${meta.className}`}><Icon className="h-3 w-3" /> {meta.label}</span>
}

function EmptyState({ label }: { label: string }) {
  return <div className="grid min-h-[360px] place-items-center rounded-xl border bg-card p-8 text-center" style={{ borderColor: 'var(--border)' }}><div><Wrench className="mx-auto mb-3 h-7 w-7 text-muted-foreground" /><p className="text-[11px] text-muted-foreground">{label}</p></div></div>
}

function ToolDetail({ tool }: { tool: api.ToolInfo }) {
  const [schemaOpen, setSchemaOpen] = useState(false)
  const schema = (tool.schema ?? {}) as ToolSchema
  const properties = Object.entries(schema.properties ?? {})
  const required = new Set(schema.required ?? [])
  const guide = getGuide(tool)

  return (
    <article className="overflow-hidden rounded-xl border bg-card" style={{ borderColor: 'var(--border)' }}>
      <div className="border-b p-5" style={{ borderColor: 'var(--border)' }}>
        <div className="flex flex-wrap items-start gap-3">
          <div className="grid h-10 w-10 shrink-0 place-items-center rounded-xl bg-chart-2/10"><Wrench className="h-5 w-5 text-chart-2" /></div>
          <div className="min-w-0 flex-1">
            <div className="flex flex-wrap items-center gap-2">
              <h2 className="font-mono text-base font-semibold tracking-tight">{tool.name}</h2>
              <span className="rounded-full border px-2 py-1 text-[9px] text-muted-foreground" style={{ borderColor: 'var(--border)' }}>{serverMeta[tool.serverName]?.label ?? tool.serverName}</span>
              <TierBadge tier={tool.tier ?? 'unknown'} />
            </div>
            <p className="mt-2 max-w-3xl text-[11px] leading-relaxed text-muted-foreground">{tool.description || 'No description was published by the MCP server.'}</p>
          </div>
        </div>
      </div>

      <div className="grid gap-4 p-5 lg:grid-cols-2">
        <section className="rounded-lg border bg-muted/20 p-4" style={{ borderColor: 'var(--border)' }}>
          <div className="flex items-center gap-2 text-[10px] font-semibold"><CheckCircle2 className="h-3.5 w-3.5 text-emerald-500" /> Before calling</div>
          <div className="mt-3 space-y-2">
            {guide.prerequisites.length === 0 ? <p className="text-[10px] leading-relaxed text-muted-foreground">No extra preconditions beyond the argument schema and sandbox policy.</p> : guide.prerequisites.map(item => <p key={item} className="flex items-start gap-2 text-[10px] leading-relaxed text-muted-foreground"><span className="mt-1 h-1 w-1 shrink-0 rounded-full bg-emerald-500" />{item}</p>)}
          </div>
        </section>
        <section className="rounded-lg border bg-muted/20 p-4" style={{ borderColor: 'var(--border)' }}>
          <div className="flex items-center gap-2 text-[10px] font-semibold"><ShieldAlert className="h-3.5 w-3.5 text-amber-500" /> Constraints & safety</div>
          <div className="mt-3 space-y-2">
            {guide.constraints.length === 0 ? <p className="text-[10px] leading-relaxed text-muted-foreground">No additional constraints were inferred from the registered tool metadata.</p> : guide.constraints.map(item => <p key={item} className="flex items-start gap-2 text-[10px] leading-relaxed text-muted-foreground"><span className="mt-1 h-1 w-1 shrink-0 rounded-full bg-amber-500" />{item}</p>)}
          </div>
        </section>
      </div>

      <section className="border-t" style={{ borderColor: 'var(--border)' }}>
        <div className="flex items-center gap-2 border-b px-5 py-3" style={{ borderColor: 'var(--border)' }}><BookOpen className="h-3.5 w-3.5 text-chart-2" /><h3 className="text-[10px] font-semibold">Arguments</h3><span className="text-[9px] text-muted-foreground">{properties.length} declared · {required.size} required</span></div>
        {properties.length === 0 ? <p className="p-5 text-[10px] text-muted-foreground">This tool accepts an empty object: <code className="rounded bg-muted px-1 py-0.5 font-mono">&#123;&#125;</code></p> : (
          <div className="divide-y" style={{ borderColor: 'var(--border)' }}>
            {properties.map(([name, rawSchema]) => {
              const property = rawSchema as JsonObject
              const description = asString(property.description)
              const defaultValue = getDefaultLabel(property.default)
              return <div key={name} className="grid gap-2 px-5 py-3 md:grid-cols-[minmax(150px,0.8fr)_minmax(110px,0.55fr)_minmax(0,2fr)] md:items-start">
                <div className="min-w-0"><div className="flex flex-wrap items-center gap-2"><code className="break-all text-[10px] font-semibold">{name}</code>{required.has(name) ? <span className="rounded bg-chart-2/10 px-1.5 py-0.5 text-[8px] font-medium text-chart-2">required</span> : <span className="rounded bg-muted px-1.5 py-0.5 text-[8px] text-muted-foreground">optional</span>}</div></div>
                <div className="font-mono text-[9px] text-chart-3">{getTypeLabel(property)}</div>
                <div className="text-[10px] leading-relaxed text-muted-foreground">{description || 'No argument description published.'}{defaultValue && <span className="ml-1 text-foreground">Default: {defaultValue}</span>}</div>
              </div>
            })}
          </div>
        )}
      </section>

      <section className="border-t" style={{ borderColor: 'var(--border)' }}>
        <button className="flex w-full items-center gap-2 px-5 py-3 text-left text-[10px] font-semibold hover:bg-accent/40" onClick={() => setSchemaOpen(previous => !previous)} aria-expanded={schemaOpen}>
          <ChevronDown className={`h-3.5 w-3.5 text-muted-foreground transition-transform ${schemaOpen ? 'rotate-180' : ''}`} /> Raw input schema
          <span className="text-[9px] font-normal text-muted-foreground">For exact JSON / generated-client use</span>
        </button>
        {schemaOpen && <pre className="max-h-[280px] overflow-auto border-t bg-muted/25 p-5 font-mono text-[9px] leading-relaxed text-muted-foreground" style={{ borderColor: 'var(--border)' }}>{JSON.stringify(schema, null, 2)}</pre>}
      </section>
    </article>
  )
}

export default function McpToolsHelper({ onClose }: { onClose: () => void }) {
  const [tools, setTools] = useState<api.ToolInfo[]>([])
  const [selectedName, setSelectedName] = useState<string | null>(null)
  const [filter, setFilter] = useState('')
  const [serverFilter, setServerFilter] = useState('all')
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)

  const loadTools = async () => {
    setLoading(true)
    setError(null)
    try {
      const next = await api.getTools()
      setTools(next)
      setSelectedName(previous => previous && next.some(tool => tool.name === previous) ? previous : next[0]?.name ?? null)
    } catch (loadError) {
      setError(loadError instanceof Error ? loadError.message : 'Unable to load the MCP tool catalog.')
    } finally {
      setLoading(false)
    }
  }

  useEffect(() => { void loadTools() }, [])

  const filteredTools = useMemo(() => {
    const needle = filter.trim().toLowerCase()
    return tools.filter(tool => (serverFilter === 'all' || tool.serverName === serverFilter) && (!needle || `${tool.name} ${tool.description ?? ''}`.toLowerCase().includes(needle)))
  }, [filter, serverFilter, tools])
  const selectedTool = tools.find(tool => tool.name === selectedName) ?? filteredTools[0] ?? null
  const servers = [...new Set(tools.map(tool => tool.serverName))].sort()
  const tierCounts = tools.reduce<Record<string, number>>((counts, tool) => { const tier = tool.tier ?? 'unknown'; counts[tier] = (counts[tier] ?? 0) + 1; return counts }, {})
  const grouped = servers.map(server => ({ server, tools: filteredTools.filter(tool => tool.serverName === server) })).filter(group => group.tools.length > 0)

  return <div className="flex min-h-0 flex-1 flex-col bg-background">
    <div className="scrollbar-sleek min-h-0 flex-1 overflow-y-auto">
      <div className="mx-auto max-w-[1440px] space-y-5 p-5 lg:p-7">
        <section className="relative overflow-hidden rounded-2xl border bg-card p-6" style={{ borderColor: 'var(--border)' }}>
          <div className="pointer-events-none absolute -right-24 -top-28 h-72 w-72 rounded-full bg-chart-2/10 blur-3xl" />
          <div className="relative flex flex-wrap items-start gap-4">
            <div className="grid h-12 w-12 shrink-0 place-items-center rounded-2xl bg-chart-2/10"><Wrench className="h-6 w-6 text-chart-2" /></div>
            <div className="min-w-0 flex-1"><div className="mb-2 flex items-center gap-2 text-[9px] font-medium uppercase tracking-[0.18em] text-chart-2"><span>MCP reference</span><span className="h-1 w-1 rounded-full bg-chart-2" /><span>Live catalog</span></div><h1 className="text-2xl font-semibold tracking-tight">Tools helper</h1><p className="mt-2 max-w-2xl text-[11px] leading-relaxed text-muted-foreground">A working reference for every MCP tool currently exposed to the assistant: what it does, when it is safe to use, and the exact arguments it accepts.</p></div>
            <button className="secondary-button" onClick={onClose}><X className="h-3.5 w-3.5" /> Back to studio</button>
          </div>
          <div className="relative mt-6 grid grid-cols-2 gap-2 sm:grid-cols-4">
            <div className="rounded-lg border bg-muted/20 p-3" style={{ borderColor: 'var(--border)' }}><div className="text-lg font-semibold tabular-nums">{tools.length}</div><div className="text-[9px] uppercase tracking-[0.14em] text-muted-foreground">tools exposed</div></div>
            <div className="rounded-lg border bg-muted/20 p-3" style={{ borderColor: 'var(--border)' }}><div className="text-lg font-semibold tabular-nums">{servers.length}</div><div className="text-[9px] uppercase tracking-[0.14em] text-muted-foreground">MCP servers</div></div>
            <div className="rounded-lg border bg-muted/20 p-3" style={{ borderColor: 'var(--border)' }}><div className="text-lg font-semibold tabular-nums text-emerald-500">{tierCounts.read ?? 0}</div><div className="text-[9px] uppercase tracking-[0.14em] text-muted-foreground">read-only</div></div>
            <div className="rounded-lg border bg-muted/20 p-3" style={{ borderColor: 'var(--border)' }}><div className="text-lg font-semibold tabular-nums text-red-500">{(tierCounts.destructive ?? 0) + (tierCounts.denied ?? 0)}</div><div className="text-[9px] uppercase tracking-[0.14em] text-muted-foreground">guarded / blocked</div></div>
          </div>
        </section>

        <div className="flex flex-col gap-3 lg:flex-row lg:items-center">
          <div className="relative min-w-0 flex-1"><Search className="pointer-events-none absolute left-3 top-1/2 h-3.5 w-3.5 -translate-y-1/2 text-muted-foreground" /><input className="field-input pl-9" value={filter} onChange={event => setFilter(event.target.value)} placeholder="Search tool names and descriptions…" />{filter && <button className="icon-button absolute right-1 top-1/2 -translate-y-1/2" onClick={() => setFilter('')} aria-label="Clear search"><X className="h-3 w-3" /></button>}</div>
          <div className="flex items-center gap-2"><select className="field-input h-9 min-w-[160px]" value={serverFilter} onChange={event => setServerFilter(event.target.value)}><option value="all">All servers</option>{servers.map(server => <option key={server} value={server}>{serverMeta[server]?.label ?? server}</option>)}</select><button className="secondary-button" onClick={() => void loadTools()} disabled={loading}><RefreshCw className={`h-3.5 w-3.5 ${loading ? 'animate-spin' : ''}`} /> Refresh</button></div>
        </div>

        {error ? <div className="flex items-start gap-2 rounded-xl border border-red-500/30 bg-red-500/8 p-4 text-[10px] text-red-700 dark:text-red-300"><AlertTriangle className="mt-0.5 h-3.5 w-3.5 shrink-0" /><div><div className="font-medium">Tool catalog unavailable</div><div className="mt-1 opacity-80">{error}</div></div></div> : loading ? <EmptyState label="Discovering MCP tools from the connected servers…" /> : tools.length === 0 ? <EmptyState label="No MCP tools are currently exposed. Start the API host and refresh." /> : filteredTools.length === 0 ? <EmptyState label="No tools match this filter." /> : (
          <div className="grid min-w-0 gap-5 xl:grid-cols-[minmax(260px,0.34fr)_minmax(0,1fr)]">
            <aside className="h-fit overflow-hidden rounded-xl border bg-card xl:sticky xl:top-5" style={{ borderColor: 'var(--border)' }}>
              <div className="border-b px-4 py-3" style={{ borderColor: 'var(--border)' }}><div className="text-[10px] font-semibold">Tool index</div><div className="mt-1 text-[9px] text-muted-foreground">{filteredTools.length} of {tools.length} visible</div></div>
              <div className="max-h-[680px] overflow-y-auto p-2">
                {grouped.map(({ server, tools: serverTools }) => { const meta = serverMeta[server] ?? serverMeta.engineering; const Icon = meta.icon; return <div key={server} className="mb-3 last:mb-0"><div className={`flex items-center gap-2 px-2 py-2 text-[9px] font-medium uppercase tracking-[0.14em] ${meta.tone}`}><Icon className="h-3 w-3" /> {meta.label}<span className="ml-auto text-muted-foreground">{serverTools.length}</span></div><div className="space-y-0.5">{serverTools.map(tool => <button key={tool.name} className={`flex w-full items-center gap-2 rounded-md px-2 py-2 text-left transition-colors ${selectedTool?.name === tool.name ? 'bg-accent text-foreground' : 'text-muted-foreground hover:bg-accent/50 hover:text-foreground'}`} onClick={() => setSelectedName(tool.name)}><span className={`h-1.5 w-1.5 shrink-0 rounded-full ${tool.tier === 'read' ? 'bg-emerald-500' : tool.tier === 'destructive' ? 'bg-red-500' : tool.tier === 'write' ? 'bg-amber-500' : 'bg-muted-foreground'}`} /><code className="min-w-0 truncate text-[10px]">{tool.name}</code></button>)}</div></div> })}
              </div>
            </aside>
            {selectedTool ? <ToolDetail tool={selectedTool} /> : <EmptyState label="Select a tool to inspect its contract." />}
          </div>
        )}
      </div>
    </div>
  </div>
}
