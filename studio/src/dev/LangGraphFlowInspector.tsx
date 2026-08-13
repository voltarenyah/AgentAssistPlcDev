import type { FlowEdge, FlowNode } from './langgraphFlowData'

type Props = { item: FlowNode | FlowEdge | null }

const isNode = (item: FlowNode | FlowEdge): item is FlowNode => 'summary' in item

export default function LangGraphFlowInspector({ item }: Props) {
  if (!item) {
    return <aside className="flow-inspector flow-inspector-empty"><span className="flow-inspector-prompt">select a node or edge</span><p>Hover for a quick read. Click or focus an element to pin its implementation detail here.</p></aside>
  }
  if (!isNode(item)) {
    return (
      <aside className="flow-inspector">
        <div className="flow-inspector-kicker">edge / route</div>
        <h2>{item.label}</h2>
        <p>{item.condition ?? 'This connector shows the state transition between two workflow units.'}</p>
        <div className="flow-inspector-meta"><span>from</span><code>{item.from}</code><span>to</span><code>{item.to}</code></div>
        <div className="flow-inspector-block"><span className="flow-inspector-label">paths</span><div className="flow-chip-row">{item.paths.map(path => <code className="flow-chip" key={path}>{path}</code>)}</div></div>
      </aside>
    )
  }
  return (
    <aside className="flow-inspector">
      <div className="flow-inspector-kicker">{item.kind}</div>
      <h2>{item.label}</h2>
      <p>{item.detail}</p>
      <div className="flow-inspector-reference"><span>source</span><code>{item.reference}</code></div>
      <div className="flow-inspector-columns">
        <div className="flow-inspector-block"><span className="flow-inspector-label">inputs</span><ul>{item.inputs.map(value => <li key={value}><code>{value}</code></li>)}</ul></div>
        <div className="flow-inspector-block"><span className="flow-inspector-label">outputs</span><ul>{item.outputs.map(value => <li key={value}><code>{value}</code></li>)}</ul></div>
      </div>
      {item.event && <div className="flow-event-callout"><span>event</span><code>{item.event}</code></div>}
      {item.safety && <div className="flow-safety"><span>guardrail</span><p>{item.safety}</p></div>}
    </aside>
  )
}
