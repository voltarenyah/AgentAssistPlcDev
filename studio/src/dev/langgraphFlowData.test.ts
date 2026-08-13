import { describe, expect, it } from 'vitest'
import { flowEdges, flowNodes, flowPaths, laneMeta, lifecycleEvents } from './langgraphFlowData'

describe('LangGraph flow data', () => {
  it('maps the four runtime lanes and the current graph topology', () => {
    expect(laneMeta.map(lane => lane.id)).toEqual(['studio', 'apiHost', 'langGraph', 'gateway'])
    const ids = flowNodes.map(node => node.id)
    expect(ids).toEqual(expect.arrayContaining([
      'lg-bootstrap', 'lg-orient', 'lg-decide', 'lg-answer', 'lg-read', 'lg-summarize', 'lg-propose', 'lg-interrupt', 'lg-end',
    ]))
    expect(flowEdges.find(edge => edge.id === 'lg-bootstrap-orient')?.condition).toContain('orientation')
    expect(flowEdges.find(edge => edge.id === 'lg-decide-propose')?.condition).toContain('mutation_proposal')
  })

  it('defines the three representative lifecycle paths and SSE order', () => {
    expect(flowPaths.map(path => path.id)).toEqual(['orientation', 'read-only', 'mutation'])
    expect(flowPaths.find(path => path.id === 'mutation')?.events).toEqual(['progress', 'state', 'interrupt', 'answer'])
    expect(lifecycleEvents.map(event => event.id)).toEqual(['progress', 'state', 'interrupt', 'answer'])
  })
})
