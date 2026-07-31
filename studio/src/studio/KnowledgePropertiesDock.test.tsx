// @vitest-environment happy-dom
import React, { act } from 'react'
import { createRoot } from 'react-dom/client'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import * as api from '@/api/client'
import KnowledgePropertiesDock from './KnowledgePropertiesDock'

vi.mock('@/api/client', () => ({
  getKnowledgeNodeProperties: vi.fn(),
  getKnowledgeEdgeProperties: vi.fn(),
}))

globalThis.IS_REACT_ACT_ENVIRONMENT = true

const mocked = api as unknown as {
  getKnowledgeNodeProperties: ReturnType<typeof vi.fn>
  getKnowledgeEdgeProperties: ReturnType<typeof vi.fn>
}

const node: api.GraphNode = { id: 'node:OB:Main', kind: 'OB', name: 'Main' }
const edge: api.GraphEdge = {
  id: 'edge:CALLS:Main->Motor',
  from_node_id: 'node:OB:Main',
  to_node_id: 'node:FB:Motor',
  type: 'CALLS',
}

const render = async (element: React.ReactNode) => {
  const host = document.createElement('div')
  document.body.appendChild(host)
  const root = createRoot(host)
  await act(async () => root.render(element))
  return { host, root }
}

beforeEach(() => {
  mocked.getKnowledgeNodeProperties.mockResolvedValue({
    properties: [
      { name: 'language', value: 'LAD' },
      { name: 'number', value: '1' },
    ],
  })
  mocked.getKnowledgeEdgeProperties.mockResolvedValue({
    properties: [{ name: 'network', value: '3' }],
  })
})

afterEach(() => {
  vi.clearAllMocks()
  document.body.innerHTML = ''
})

describe('KnowledgePropertiesDock', () => {
  it('shows an empty hint when nothing is selected', async () => {
    const { host } = await render(
      <KnowledgePropertiesDock projectName="PLC_1" node={null} edge={null} hidden={false} />,
    )

    expect(host.textContent).toContain('Select a node or edge to inspect its properties')
    expect(mocked.getKnowledgeNodeProperties).not.toHaveBeenCalled()
    expect(mocked.getKnowledgeEdgeProperties).not.toHaveBeenCalled()
  })

  it('loads and displays node properties for the selected node', async () => {
    const { host } = await render(
      <KnowledgePropertiesDock projectName="PLC_1" node={node} edge={null} hidden={false} />,
    )

    expect(mocked.getKnowledgeNodeProperties).toHaveBeenCalledWith('PLC_1', 'node:OB:Main')
    expect(host.textContent).toContain('language')
    expect(host.textContent).toContain('LAD')
  })

  it('displays both edge and node properties when both are selected', async () => {
    const { host } = await render(
      <KnowledgePropertiesDock projectName="PLC_1" node={node} edge={edge} hidden={false} />,
    )

    expect(mocked.getKnowledgeEdgeProperties).toHaveBeenCalledWith('PLC_1', 'edge:CALLS:Main->Motor')
    expect(mocked.getKnowledgeNodeProperties).toHaveBeenCalledWith('PLC_1', 'node:OB:Main')
    expect(host.textContent).toContain('network')
    expect(host.textContent).toContain('LAD')
    expect(host.textContent).toContain('node:OB:Main → node:FB:Motor')
  })

  it('shows an error when the properties request fails', async () => {
    mocked.getKnowledgeNodeProperties.mockRejectedValue(new Error('boom'))
    const { host } = await render(
      <KnowledgePropertiesDock projectName="PLC_1" node={node} edge={null} hidden={false} />,
    )

    expect(host.textContent).toContain('boom')
  })
})
