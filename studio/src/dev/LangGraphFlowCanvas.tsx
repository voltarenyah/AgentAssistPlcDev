import type { FlowEdge, FlowNode, FlowPathId } from './langgraphFlowData'
import { laneMeta } from './langgraphFlowData'

type Props = {
  nodes: FlowNode[]
  edges: FlowEdge[]
  activePath: FlowPathId
  activeNodeIds: string[]
  activeEdgeIds: string[]
  hoveredId: string | null
  selectedId: string | null
  onHover: (id: string | null) => void
  onSelect: (id: string) => void
}

const laneClass = (lane: FlowNode['lane']) => `flow-lane flow-lane-${lane}`

export default function LangGraphFlowCanvas({
  nodes, edges, activePath, activeNodeIds, activeEdgeIds, hoveredId, selectedId, onHover, onSelect,
}: Props) {
  const visibleEdges = activePath === 'all' ? edges : edges.filter(edge => activeEdgeIds.includes(edge.id))
  return (
    <div className="flow-canvas" data-active-path={activePath}>
      <div className="flow-canvas-grid" aria-hidden="true" />
      <div className="flow-lanes">
        {laneMeta.map(lane => (
          <section className={laneClass(lane.id)} key={lane.id} aria-label={`${lane.label} lane`}>
            <div className="flow-lane-heading">
              <span>{lane.label}</span>
              <small>{lane.eyebrow}</small>
            </div>
          </section>
        ))}
      </div>
      <svg className="flow-connectors" viewBox="0 0 1000 1240" preserveAspectRatio="none" aria-hidden="true">
        <defs>
          <marker id="flow-arrow" viewBox="0 0 10 10" refX="8" refY="5" markerWidth="6" markerHeight="6" orient="auto-start-reverse">
            <path d="M 0 0 L 10 5 L 0 10 z" fill="currentColor" />
          </marker>
        </defs>
        {visibleEdges.map(edge => {
          const from = nodes.find(node => node.id === edge.from)
          const to = nodes.find(node => node.id === edge.to)
          if (!from || !to) return null
          const x1 = from.position.x + 78
          const y1 = from.position.y + 36
          const x2 = to.position.x + 78
          const y2 = to.position.y + 36
          const midX = (x1 + x2) / 2
          const active = activePath === 'all' || activeEdgeIds.includes(edge.id)
          return (
            <g className={`flow-edge ${active ? 'is-active' : 'is-muted'} ${hoveredId === edge.id ? 'is-hovered' : ''}`} key={edge.id}>
              <path
                className="flow-edge-hit"
                d={`M ${x1} ${y1} C ${midX} ${y1}, ${midX} ${y2}, ${x2} ${y2}`}
                onMouseEnter={() => onHover(edge.id)}
                onMouseLeave={() => onHover(null)}
                onClick={() => onSelect(edge.id)}
              />
              <path className="flow-edge-line" markerEnd="url(#flow-arrow)" d={`M ${x1} ${y1} C ${midX} ${y1}, ${midX} ${y2}, ${x2} ${y2}`} />
              <text className="flow-edge-label" x={midX} y={(y1 + y2) / 2 - 5}>{edge.label}</text>
            </g>
          )
        })}
      </svg>
      <div className="flow-node-layer">
        {nodes.map(node => {
          const active = activePath === 'all' || activeNodeIds.includes(node.id)
          const focused = hoveredId === node.id || selectedId === node.id
          return (
            <button
              type="button"
              className={`flow-node flow-node-${node.kind.replace(/\s+/g, '-').toLowerCase()} ${active ? 'is-active' : 'is-muted'} ${focused ? 'is-focused' : ''}`}
              data-node-id={node.id}
              data-active={active}
              key={node.id}
              style={{ left: `${node.position.x / 10}%`, top: node.position.y }}
              aria-label={`${node.label}: ${node.summary}`}
              onMouseEnter={() => onHover(node.id)}
              onMouseLeave={() => onHover(null)}
              onFocus={() => onHover(node.id)}
              onBlur={() => onHover(null)}
              onClick={() => onSelect(node.id)}
            >
              <span className="flow-node-kind">{node.kind}</span>
              <strong>{node.label}</strong>
              <span className="flow-node-summary">{node.summary}</span>
            </button>
          )
        })}
      </div>
      {hoveredId && (
        <div className="flow-hover-note" role="status">
          {nodes.find(node => node.id === hoveredId)?.summary ?? edges.find(edge => edge.id === hoveredId)?.condition ?? 'Click to pin details'}
        </div>
      )}
    </div>
  )
}
