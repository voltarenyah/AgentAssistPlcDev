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
  mocked.getKnowledgeNodes.mockResolvedValue({ nodes, truncated: false, totalCount: nodes.length })
  mocked.getKnowledgeEdgeTypes.mockResolvedValue({ types: ['CALLS', 'CONTAINS'] })
  mocked.getKnowledgeEdges.mockResolvedValue({ edges, truncated: false, totalCount: edges.length })
})

afterEach(() => {
  vi.clearAllMocks()
  document.body.innerHTML = ''
})

describe('NodeEdgesView', () => {
  it('loads nodes and all edges on mount', async () => {
    const { host } = await render(<NodeEdgesView context={context} projectName="PLC_1" />)

    expect(mocked.getKnowledgeEdges).toHaveBeenCalledWith(context, undefined, undefined, undefined, 100, undefined)
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

    expect(mocked.getKnowledgeEdges).toHaveBeenCalledWith(context, 'node:FB:Motor', undefined, undefined, 100, undefined)
    expect(onNodeSelect).toHaveBeenCalledWith(nodes[1])
    expect(onEdgeSelect).toHaveBeenCalledWith(null)
  })

  it('reports edge selection when an edge row is clicked', async () => {
    const onEdgeSelect = vi.fn()
    const { host } = await render(<NodeEdgesView context={context} projectName="PLC_1" onEdgeSelect={onEdgeSelect} />)

    await act(async () => rowByText(host, 'edge:CALLS:Main->Motor')?.dispatchEvent(new MouseEvent('click', { bubbles: true })))

    expect(onEdgeSelect).toHaveBeenCalledWith(edges[0])
  })

  it('searches nodes on the server after a debounce', async () => {
    const { host } = await render(<NodeEdgesView context={context} projectName="PLC_1" />)
    mocked.getKnowledgeNodes.mockClear()
    mocked.getKnowledgeNodes.mockResolvedValue({ nodes: [nodes[1]], truncated: false, totalCount: 1 })
    const input = host.querySelector<HTMLInputElement>('input[placeholder="Search nodes..."]')!

    await act(async () => typeInto(input, 'motor'))

    // no request before the debounce elapses
    expect(mocked.getKnowledgeNodes).not.toHaveBeenCalled()

    await act(async () => { await new Promise(resolve => setTimeout(resolve, 350)) })

    expect(mocked.getKnowledgeNodes).toHaveBeenCalledWith(context, undefined, 'motor', 100, undefined)
    expect(rowByText(host, 'Main', 0)).toBeUndefined()
    expect(rowByText(host, 'Motor', 0)).toBeTruthy()
  })

  it('ignores a stale node response after a newer search request', async () => {
    let resolveInitial!: (value: { nodes: api.GraphNode[]; truncated: boolean; totalCount: number }) => void
    const initialResponse = new Promise<{ nodes: api.GraphNode[]; truncated: boolean; totalCount: number }>(resolve => {
      resolveInitial = resolve
    })
    mocked.getKnowledgeNodes.mockReset()
    mocked.getKnowledgeNodes.mockReturnValueOnce(initialResponse)
    mocked.getKnowledgeNodes.mockResolvedValueOnce({ nodes: [nodes[1]], truncated: false, totalCount: 1 })

    const { host } = await render(<NodeEdgesView context={context} projectName="PLC_1" />)
    const input = host.querySelector<HTMLInputElement>('input[placeholder="Search nodes..."]')!

    await act(async () => typeInto(input, 'motor'))
    await act(async () => { await new Promise(resolve => setTimeout(resolve, 350)) })
    await act(async () => resolveInitial({ nodes: [nodes[0]], truncated: false, totalCount: 1 }))

    expect(rowByText(host, 'Motor', 0)).toBeTruthy()
    expect(rowByText(host, 'Main', 0)).toBeUndefined()
  })

  it('searches edges on the server after a debounce', async () => {
    const { host } = await render(<NodeEdgesView context={context} projectName="PLC_1" />)
    mocked.getKnowledgeEdges.mockClear()
    mocked.getKnowledgeEdges.mockResolvedValue({ edges: [edges[1]], truncated: false, totalCount: 1 })
    const input = host.querySelector<HTMLInputElement>('input[placeholder="Search edges..."]')!

    await act(async () => typeInto(input, 'contains'))
    await act(async () => { await new Promise(resolve => setTimeout(resolve, 350)) })

    expect(mocked.getKnowledgeEdges).toHaveBeenCalledWith(context, undefined, undefined, 'contains', 100, undefined)
    expect(rowByText(host, 'edge:CALLS:Main->Motor')).toBeUndefined()
    expect(rowByText(host, 'edge:CONTAINS:Motor->Var')).toBeTruthy()
  })

  it('shows the truncated indicator and total count for nodes', async () => {
    mocked.getKnowledgeNodes.mockReset()
    mocked.getKnowledgeNodes.mockResolvedValue({ nodes, truncated: true, totalCount: 5 })

    const { host } = await render(<NodeEdgesView context={context} projectName="PLC_1" />)

    expect(host.textContent).toContain('2 of 5 nodes')
    expect(host.textContent).toContain('(truncated)')
  })

  it('does not render more than the bounded page when the API returns extra rows', async () => {
    const manyNodes = Array.from({ length: 201 }, (_, index) => ({
      id: `node:${index}`,
      kind: 'FB',
      name: `Node ${index}`,
    }))
    const manyEdges = Array.from({ length: 201 }, (_, index) => ({
      id: `edge:${index}`,
      from_node_id: `node:${index}`,
      to_node_id: `node:${index + 1}`,
      type: 'CALLS',
    }))
    mocked.getKnowledgeNodes.mockReset()
    mocked.getKnowledgeNodes.mockResolvedValue({ nodes: manyNodes, truncated: true, totalCount: 1000 })
    mocked.getKnowledgeEdges.mockReset()
    mocked.getKnowledgeEdges.mockResolvedValue({ edges: manyEdges, truncated: true, totalCount: 1000 })

    const { host } = await render(<NodeEdgesView context={context} projectName="PLC_1" />)

    expect(host.querySelectorAll('table')[0]?.querySelectorAll('tbody tr')).toHaveLength(200)
    expect(host.querySelectorAll('table')[1]?.querySelectorAll('tbody tr')).toHaveLength(200)
    expect(host.textContent).toContain('Showing the first 200 nodes')
    expect(host.textContent).toContain('Showing the first 200 edges')
  })

  it('appends the next page when Load more is clicked', async () => {
    mocked.getKnowledgeNodes.mockReset()
    mocked.getKnowledgeNodes.mockResolvedValueOnce({ nodes: [nodes[0]], truncated: true, totalCount: 2 })
    mocked.getKnowledgeNodes.mockResolvedValue({ nodes: [nodes[1]], truncated: false, totalCount: 2 })
    const { host } = await render(<NodeEdgesView context={context} projectName="PLC_1" />)
    const button = Array.from(host.querySelectorAll('button')).find(b => b.textContent === 'Load more')!
    expect(button).toBeTruthy()

    await act(async () => button.dispatchEvent(new MouseEvent('click', { bubbles: true })))

    expect(mocked.getKnowledgeNodes).toHaveBeenLastCalledWith(context, undefined, undefined, 100, 1)
    expect(rowByText(host, 'Main', 0)).toBeTruthy()
    expect(rowByText(host, 'Motor', 0)).toBeTruthy()
    expect(host.textContent).toContain('2 of 2 nodes')
    expect(Array.from(host.querySelectorAll('button')).some(b => b.textContent === 'Load more')).toBe(false)
  })

  it('resets the load-more state when a newer filter request supersedes it', async () => {
    let resolveMore!: (value: { nodes: api.GraphNode[]; truncated: boolean; totalCount: number }) => void
    const moreResponse = new Promise<{ nodes: api.GraphNode[]; truncated: boolean; totalCount: number }>(resolve => {
      resolveMore = resolve
    })
    mocked.getKnowledgeNodes.mockReset()
    mocked.getKnowledgeNodes.mockResolvedValueOnce({ nodes: [nodes[0]], truncated: true, totalCount: 2 })
    mocked.getKnowledgeNodes.mockReturnValueOnce(moreResponse)
    mocked.getKnowledgeNodes.mockResolvedValueOnce({ nodes: [nodes[1]], truncated: true, totalCount: 2 })

    const { host } = await render(<NodeEdgesView context={context} projectName="PLC_1" />)
    const loadMore = () => Array.from(host.querySelectorAll('button')).find(b => b.textContent === 'Load more' || b.textContent === 'Loading...')!

    await act(async () => loadMore().dispatchEvent(new MouseEvent('click', { bubbles: true })))
    expect(loadMore().disabled).toBe(true)

    const select = host.querySelector('select')!
    const setter = Object.getOwnPropertyDescriptor(window.HTMLSelectElement.prototype, 'value')!.set!
    await act(async () => {
      setter.call(select, 'FB')
      select.dispatchEvent(new Event('change', { bubbles: true }))
    })

    expect(loadMore().disabled).toBe(false)
    await act(async () => resolveMore({ nodes: [nodes[1]], truncated: false, totalCount: 2 }))
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

    expect(mocked.getKnowledgeNodes).toHaveBeenCalledWith(context, 'FB', undefined, 100, undefined)
  })
})
