import { useMemo, useState } from 'react'
import LangGraphFlowCanvas from './LangGraphFlowCanvas'
import LangGraphFlowInspector from './LangGraphFlowInspector'
import {
  findFlowItem,
  flowEdges,
  flowNodes,
  flowPaths,
  getActiveFlow,
  lifecycleEvents,
  type FlowPathId,
} from './langgraphFlowData'
import './langgraph-flow.css'

export const isLangGraphFlowDevRoute = (url: string, isDev: boolean) => isDev && new URL(url, 'http://localhost').searchParams.get('dev') === 'langgraph-flow'

export default function LangGraphFlowPage() {
  const [activePath, setActivePath] = useState<FlowPathId>('all')
  const [hoveredId, setHoveredId] = useState<string | null>(null)
  const [selectedId, setSelectedId] = useState<string | null>(null)
  const activeFlow = useMemo(() => getActiveFlow(activePath), [activePath])
  const selectedItem = findFlowItem(selectedId ?? hoveredId ?? '')
  const currentPath = flowPaths.find(path => path.id === activePath)
  const visibleEvents = currentPath?.events ?? lifecycleEvents.map(event => event.id)

  return (
    <main className="langgraph-flow-page">
      <div className="flow-shell">
        <div className="flow-topline">
          <div className="flow-brand"><strong>AgentAssist PlcDev</strong> / runtime map</div>
          <div className="flow-badge">DEV EXPLAINER · STATIC MAP</div>
        </div>
        <header className="flow-hero">
          <h1>How the <em>LangGraph</em> workflow moves.</h1>
          <p>Trace one assistant turn from the Studio request, through the ApiHost event bridge and the Python graph, into the C# gateway — then back again. Hover for a quick read. Click to pin source-aware detail.</p>
        </header>
        <div className="flow-controls">
          <span className="flow-control-label">highlight a path</span>
          <div className="flow-paths" role="group" aria-label="Workflow path selection">
            <button type="button" className={`flow-path-button ${activePath === 'all' ? 'is-selected' : ''}`} aria-pressed={activePath === 'all'} onClick={() => setActivePath('all')}>All paths</button>
            {flowPaths.map(path => <button type="button" key={path.id} className={`flow-path-button ${activePath === path.id ? 'is-selected' : ''}`} aria-pressed={activePath === path.id} onClick={() => setActivePath(path.id)}>{path.label}</button>)}
          </div>
          <div className="flow-legend" aria-label="Legend"><span>active route</span><span className="legend-approval">human gate</span><span className="legend-event">event bridge</span></div>
        </div>
        {currentPath && <div className="flow-path-description">{currentPath.description}</div>}
        <div className="flow-workspace">
          <LangGraphFlowCanvas
            nodes={flowNodes}
            edges={flowEdges}
            activePath={activePath}
            activeNodeIds={activeFlow.nodeIds}
            activeEdgeIds={activeFlow.edgeIds}
            hoveredId={hoveredId}
            selectedId={selectedId}
            onHover={setHoveredId}
            onSelect={setSelectedId}
          />
          <LangGraphFlowInspector item={selectedItem} />
        </div>
        <section className="flow-event-strip" aria-label="SSE event sequence">
          <h2>ApiHost → Studio / event sequence</h2>
          <div className="flow-events">
            {lifecycleEvents.map(event => <div className={`flow-event ${visibleEvents.includes(event.id) ? 'is-visible' : ''} ${event.tone === 'approval' ? 'is-approval' : ''}`} key={event.id}><strong>{event.label}</strong><p>{event.detail}</p></div>)}
          </div>
        </section>
        <p className="flow-footer">This is a static developer map of the current implementation. It makes no API calls and cannot execute a mutation. When graph topology, routing, or the SSE contract changes, update <code>studio/src/dev/langgraphFlowData.ts</code> alongside the source change.</p>
      </div>
    </main>
  )
}
