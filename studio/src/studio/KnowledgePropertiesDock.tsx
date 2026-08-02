import { useEffect, useState } from 'react'
import { Database, Loader2 } from 'lucide-react'
import * as api from '@/api/client'

type Props = {
  context: api.KnowledgeGraphContext
  node: api.GraphNode | null
  edge: api.GraphEdge | null
  hidden: boolean
}

type PropertiesState = {
  properties: api.GraphProperty[]
  loading: boolean
  error: string | null
}

function useProperties(kind: 'node' | 'edge', context: api.KnowledgeGraphContext, id: string | null): PropertiesState {
  const [properties, setProperties] = useState<api.GraphProperty[]>([])
  const [loading, setLoading] = useState(false)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    if (!id) {
      setProperties([])
      setError(null)
      setLoading(false)
      return
    }
    let cancelled = false
    setLoading(true)
    setError(null)
    const request = kind === 'node'
      ? api.getKnowledgeNodeProperties(context, id)
      : api.getKnowledgeEdgeProperties(context, id)
    request
      .then(data => { if (!cancelled) setProperties(data.properties) })
      .catch(e => { if (!cancelled) setError(e instanceof Error ? e.message : 'Failed to load properties') })
      .finally(() => { if (!cancelled) setLoading(false) })
    return () => { cancelled = true }
  }, [kind, context, id])

  return { properties, loading, error }
}

function PropertySection({ title, badge, badgeColor, subtitle, state }: {
  title: string
  badge: string
  badgeColor: string
  subtitle: string
  state: PropertiesState
}) {
  return (
    <section className="rounded-md border bg-background" style={{ borderColor: 'var(--border)' }}>
      <div className="border-b px-2 py-1.5" style={{ borderColor: 'var(--border)' }}>
        <div className="flex items-center gap-1.5">
          <span className="rounded px-1 text-[8px] font-medium"
            style={{ background: `${badgeColor}22`, color: badgeColor }}>
            {badge}
          </span>
          <span className="min-w-0 flex-1 truncate text-[10px] font-medium">{title}</span>
        </div>
        <div className="mt-0.5 truncate font-mono text-[8px] text-muted-foreground">{subtitle}</div>
      </div>
      {state.loading ? (
        <div className="flex items-center gap-2 px-2 py-3">
          <Loader2 className="h-3 w-3 animate-spin text-muted-foreground" />
          <span className="text-[9px] text-muted-foreground">Loading properties...</span>
        </div>
      ) : state.error ? (
        <div className="px-2 py-3 text-[9px]" style={{ color: 'var(--destructive)' }}>{state.error}</div>
      ) : state.properties.length === 0 ? (
        <div className="px-2 py-3 text-[9px] text-muted-foreground">No properties</div>
      ) : (
        <div className="divide-y" style={{ borderColor: 'var(--border)' }}>
          {state.properties.map(property => (
            <div key={property.name} className="px-2 py-1.5">
              <div className="text-[8px] uppercase tracking-[0.12em] text-muted-foreground">{property.name}</div>
              <div className="mt-0.5 break-all font-mono text-[10px]">{property.value}</div>
            </div>
          ))}
        </div>
      )}
    </section>
  )
}

export default function KnowledgePropertiesDock({ context, node, edge, hidden }: Props) {
  const nodeState = useProperties('node', context, node?.id ?? null)
  const edgeState = useProperties('edge', context, edge?.id ?? null)

  return (
    <aside
      hidden={hidden}
      className="flex h-full w-full shrink-0 flex-col border-l bg-card"
      style={{ borderColor: 'var(--border)' }}
    >
      <div className="flex h-10 items-center gap-2 border-b px-3" style={{ borderColor: 'var(--border)' }}>
        <Database className="h-3.5 w-3.5 text-chart-3" />
        <h2 className="text-[10px] font-semibold">Properties</h2>
      </div>
      <div className="scrollbar-sleek min-h-0 flex-1 overflow-y-auto p-2">
        {!node && !edge ? (
          <div className="grid h-full place-items-center px-5 text-center text-[10px] text-muted-foreground">
            <div>
              <Database className="mx-auto mb-2 h-5 w-5" />
              Select a node or edge to inspect its properties
            </div>
          </div>
        ) : (
          <div className="space-y-2">
            {edge && (
              <PropertySection
                title={edge.id}
                badge={edge.type}
                badgeColor="#f97316"
                subtitle={`${edge.from_node_id} → ${edge.to_node_id}`}
                state={edgeState}
              />
            )}
            {node && (
              <PropertySection
                title={node.name}
                badge={node.kind}
                badgeColor="#3b82f6"
                subtitle={node.id}
                state={nodeState}
              />
            )}
          </div>
        )}
      </div>
    </aside>
  )
}
