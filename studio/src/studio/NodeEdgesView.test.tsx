// @vitest-environment happy-dom
import React, { act } from 'react'
import { createRoot } from 'react-dom/client'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import * as api from '@/api/client'
import NodeEdgesView from './NodeEdgesView'

vi.mock('@/api/client', () => ({
  getKnowledgeNodeKinds: vi.fn(),
  getKnowledgeNodes: vi.fn(),
  getKnowledgeEdgeTypes: vi.fn(),
  getKnowledgeEdges: vi.fn(),
  getKnowledgeNodeProperties: vi.fn(),
  getKnowledgeEdgeProperties: vi.fn(),
}))

globalThis.IS_REACT_ACT_ENVIRONMENT = true

const context: api.KnowledgeGraphContext = { workbenchId: 'wb1', worktreeId: 'wt1', deviceId: 'dev1' }

const nodes: api.GraphNode[] = [
  { id: 'node:OB:Main', kind: 'OB', name: 'Main' },
  { id: 'node:FB:Motor', kind: 'FB', name: 'Motor' },
]

const edges: api.GraphEdge[] = [
  { id: 'edge:CALLS:Main->Motor', from_node_id: 'node:OB:Main', to_node_id: 'node:FB:Motor', type: 'CALLS' },
  { id: 'edge:CONTAINS:Motor->Var', from_node_id: 'node:FB:Motor', to_node_id: 'node:Variable:Speed', type: 'CONTAINS' },
]

const mocked = api as unknown as {
  getKnowledgeNodeKinds: ReturnType<typeof vi.fn>
  getKnowledgeNodes: ReturnType<typeof vi.fn>
  getKnowledgeEdgeTypes: ReturnType<typeof vi.fn>
  getKnowledgeEdges: ReturnType<typeof vi.fn>
}

const render = async (element: React.ReactNode) => {
  const host = document.createElement('div')
  document.body.appendChild(host)
  const root = createRoot(host)
  await act(async () => root.render(element))
  return { host, root }
}

const rowByText = (host: HTMLElement, text: string, tableIndex?: number) => {
  const scope = tableIndex === undefined
    ? host
    : host.querySelectorAll('table')[tableIndex] ?? null
  return Array.from(scope?.querySelectorAll('tbody tr') ?? []).find(row => row.textContent?.includes(text))
}

const typeInto = (input: HTMLInputElement, value: string) => {
  const setter = Object.getOwnPropertyDescriptor(window.HTMLInputElement.prototype, 'value')!.set!
  setter.call(input, value)
  input.dispatchEvent(new Event('input', { bubbles: true }))
}

beforeEach(() => {
  mocked.getKnowledgeNodeKinds.mockResolvedValue({ kinds: ['OB', 'FB'] })
  mocked.getKnowledgeNodes.mockResolvedValue({ nodes })
  mocked.getKnowledgeEdgeTypes.mockResolvedValue({ types: ['CALLS', 'CONTAINS'] })
  mocked.getKnowledgeEdges.mockResolvedValue({ edges, truncated: false })
})

afterEach(() => {
  vi.clearAllMocks()
  document.body.innerHTML = ''
})

describe('NodeEdgesView', () => {
  it('loads nodes and all edges on mount', async () => {
    const { host } = await render(<NodeEdgesView context={context} projectName="PLC_1" />)

    expect(mocked.getKnowledgeEdges).toHaveBeenCalledWith(context, undefined, undefined)
    expect(rowByText(host, 'Main')).toBeTruthy()
    expect(rowByText(host, 'edge:CALLS:Main->Motor')).toBeTruthy()
    expect(rowByText(host, 'edge:CONTAINS:Motor->Var')).toBeTruthy()
  })

  it('refetches edges related to the clicked node and reports the selection', async () => {
    const onNodeSelect = vi.fn()
    const onEdgeSelect = vi.fn()
    const { host } = await render(
      <NodeEdgesView context={context} projectName="PLC_1" onNodeSelect={onNodeSelect} onEdgeSelect={onEdgeSelect} />,
    )
    mocked.getKnowledgeEdges.mockClear()

    await act(async () => rowByText(host, 'Motor', 0)?.dispatchEvent(new MouseEvent('click', { bubbles: true })))

    expect(mocked.getKnowledgeEdges).toHaveBeenCalledWith(context, 'node:FB:Motor', undefined)
    expect(onNodeSelect).toHaveBeenCalledWith(nodes[1])
    expect(onEdgeSelect).toHaveBeenCalledWith(null)
  })

  it('reports edge selection when an edge row is clicked', async () => {
    const onEdgeSelect = vi.fn()
    const { host } = await render(<NodeEdgesView context={context} projectName="PLC_1" onEdgeSelect={onEdgeSelect} />)

    await act(async () => rowByText(host, 'edge:CALLS:Main->Motor')?.dispatchEvent(new MouseEvent('click', { bubbles: true })))

    expect(onEdgeSelect).toHaveBeenCalledWith(edges[0])
  })

  it('filters node rows by the node search box', async () => {
    const { host } = await render(<NodeEdgesView context={context} projectName="PLC_1" />)
    const input = host.querySelector<HTMLInputElement>('input[placeholder="Search nodes..."]')!

    await act(async () => typeInto(input, 'motor'))

    expect(rowByText(host, 'Main', 0)).toBeUndefined()
    expect(rowByText(host, 'Motor', 0)).toBeTruthy()
  })

  it('filters edge rows by the edge search box', async () => {
    const { host } = await render(<NodeEdgesView context={context} projectName="PLC_1" />)
    const input = host.querySelector<HTMLInputElement>('input[placeholder="Search edges..."]')!

    await act(async () => typeInto(input, 'contains'))

    expect(rowByText(host, 'edge:CALLS:Main->Motor')).toBeUndefined()
    expect(rowByText(host, 'edge:CONTAINS:Motor->Var')).toBeTruthy()
  })

  it('refetches nodes when the kind filter changes', async () => {
    const { host } = await render(<NodeEdgesView context={context} projectName="PLC_1" />)
    mocked.getKnowledgeNodes.mockClear()
    const select = host.querySelector('select')!

    const setter = Object.getOwnPropertyDescriptor(window.HTMLSelectElement.prototype, 'value')!.set!
    await act(async () => {
      setter.call(select, 'FB')
      select.dispatchEvent(new Event('change', { bubbles: true }))
    })

    expect(mocked.getKnowledgeNodes).toHaveBeenCalledWith(context, 'FB')
  })
})
