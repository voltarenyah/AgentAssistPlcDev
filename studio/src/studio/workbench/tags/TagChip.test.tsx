// @vitest-environment happy-dom
import React, { act } from 'react'
import { createRoot } from 'react-dom/client'
import { afterEach, describe, expect, it, vi } from 'vitest'
import type { TagNode } from '@/api/client'
import TagChip from './TagChip'

const nodes: TagNode[] = [
  { tagId: 'a', parentTagId: null, name: 'Machine', normalizedName: 'machine' },
  { tagId: 'b', parentTagId: 'a', name: 'Press', normalizedName: 'press' },
]

afterEach(() => { document.body.innerHTML = '' })

describe('TagChip', () => {
  it('renders a full path and labelled remove control for direct tags', async () => {
    const onRemove = vi.fn()
    const host = document.createElement('div')
    document.body.appendChild(host)
    const root = createRoot(host)
    await act(async () => root.render(<TagChip node={nodes[1]} nodes={nodes} removable onRemove={onRemove} />))
    expect(host.textContent).toContain('Machine/Press')
    const remove = host.querySelector('button[aria-label="Remove tag Machine/Press"]')
    expect(remove).toBeTruthy()
    await act(async () => remove?.dispatchEvent(new MouseEvent('click', { bubbles: true })))
    expect(onRemove).toHaveBeenCalledWith('b')
    await act(async () => root.unmount())
  })

  it('labels inherited tags and exposes no remove action', async () => {
    const host = document.createElement('div')
    document.body.appendChild(host)
    const root = createRoot(host)
    await act(async () => root.render(<TagChip node={nodes[1]} nodes={nodes} inherited removable onRemove={() => {}} />))
    expect(host.textContent).toContain('Inherited from Project')
    expect(host.querySelector('button[aria-label^="Remove tag"]')).toBeNull()
    await act(async () => root.unmount())
  })
})
