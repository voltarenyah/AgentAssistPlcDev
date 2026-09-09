// @vitest-environment happy-dom
import React, { act } from 'react'
import { createRoot } from 'react-dom/client'
import { afterEach, describe, expect, it, vi } from 'vitest'
import type { TagNode } from '@/api/client'
import TagTree from './TagTree'

const nodes: TagNode[] = [
  { tagId: 'a', parentTagId: null, name: 'Machine', normalizedName: 'machine' },
  { tagId: 'b', parentTagId: 'a', name: 'Press', normalizedName: 'press' },
  { tagId: 'c', parentTagId: 'b', name: 'Hydraulic', normalizedName: 'hydraulic' },
]

afterEach(() => { document.body.innerHTML = '' })

describe('TagTree', () => {
  it('controls expansion by stable IDs and selects a deep node', async () => {
    const onExpandedChange = vi.fn()
    const onSelect = vi.fn()
    const host = document.createElement('div')
    document.body.appendChild(host)
    const root = createRoot(host)
    await act(async () => root.render(<TagTree nodes={nodes} expandedIds={['a', 'b']} onExpandedChange={onExpandedChange} onSelect={onSelect} />))
    expect(host.querySelector('[role="treeitem"][aria-expanded="true"]')).toBeTruthy()
    const collapse = host.querySelector('button[aria-label="Collapse Machine/Press"]') as HTMLButtonElement
    await act(async () => collapse.click())
    expect(onExpandedChange).toHaveBeenCalledWith(['a'])
    await act(async () => host.querySelector('button[aria-label="Select Machine/Press/Hydraulic"]')?.click())
    expect(onSelect).toHaveBeenCalledWith(nodes[2])
    await act(async () => root.unmount())
  })
})
