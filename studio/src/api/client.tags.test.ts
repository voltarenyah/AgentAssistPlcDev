import { afterEach, describe, expect, it, vi } from 'vitest'
import {
  assignWorkbenchTag,
  assignWorktreeTag,
  createTagPath,
  getWorkbenchTags,
  getWorktreeTags,
  renameTag,
  searchWorkbenches,
  unassignWorkbenchTag,
  unassignWorktreeTag,
} from './client'

afterEach(() => {
  vi.unstubAllGlobals()
})

describe('workbench tag client', () => {
  it('serializes taxonomy create and rename requests and returns nodes', async () => {
    const node = { tagId: 'tag-1', parentTagId: null, name: 'Area', normalizedName: 'area' }
    const fetchMock = vi.fn()
      .mockResolvedValueOnce(new Response(JSON.stringify(node), { status: 201 }))
      .mockResolvedValueOnce(new Response(JSON.stringify({ ...node, name: 'Zone' }), { status: 200 }))
    vi.stubGlobal('fetch', fetchMock)

    await expect(createTagPath('Area/Zone')).resolves.toEqual(node)
    await expect(renameTag('tag/1', 'Zone')).resolves.toEqual({ ...node, name: 'Zone' })

    expect(fetchMock.mock.calls[0]?.[0]).toBe('/api/tags/path')
    expect(fetchMock.mock.calls[0]?.[1]).toMatchObject({ method: 'POST' })
    expect(JSON.parse(String((fetchMock.mock.calls[0]?.[1] as RequestInit).body))).toEqual({ path: 'Area/Zone' })
    expect(fetchMock.mock.calls[1]?.[0]).toBe('/api/tags/tag%2F1')
    expect(fetchMock.mock.calls[1]?.[1]).toMatchObject({ method: 'PATCH' })
    expect(JSON.parse(String((fetchMock.mock.calls[1]?.[1] as RequestInit).body))).toEqual({ name: 'Zone' })
  })

  it('uses encoded Workbench tag routes and handles 204 mutations', async () => {
    const fetchMock = vi.fn()
      .mockResolvedValueOnce(new Response(JSON.stringify({ direct: ['tag-1'], inherited: [], effective: ['tag-1'] }), { status: 200 }))
      .mockResolvedValueOnce(new Response(null, { status: 204 }))
      .mockResolvedValueOnce(new Response(null, { status: 204 }))
    vi.stubGlobal('fetch', fetchMock)

    await expect(getWorkbenchTags('wb/1')).resolves.toEqual({ direct: ['tag-1'], inherited: [], effective: ['tag-1'] })
    await expect(assignWorkbenchTag('wb/1', 'tag/1')).resolves.toBeUndefined()
    await expect(unassignWorkbenchTag('wb/1', 'tag/1')).resolves.toBeUndefined()

    expect(fetchMock.mock.calls[0]?.[0]).toBe('/api/workbenches/wb%2F1/tags')
    expect(fetchMock.mock.calls[1]?.[0]).toBe('/api/workbenches/wb%2F1/tags/tag%2F1')
    expect(fetchMock.mock.calls[1]?.[1]).toMatchObject({ method: 'POST' })
    expect(fetchMock.mock.calls[2]?.[0]).toBe('/api/workbenches/wb%2F1/tags/tag%2F1')
    expect(fetchMock.mock.calls[2]?.[1]).toMatchObject({ method: 'DELETE' })
  })

  it('uses encoded Worktree tag routes for read and 204 mutations', async () => {
    const projection = { direct: ['tag-2'], inherited: ['tag-1'], effective: ['tag-1', 'tag-2'] }
    const fetchMock = vi.fn()
      .mockResolvedValueOnce(new Response(JSON.stringify(projection), { status: 200 }))
      .mockResolvedValueOnce(new Response(null, { status: 204 }))
      .mockResolvedValueOnce(new Response(null, { status: 204 }))
    vi.stubGlobal('fetch', fetchMock)

    await expect(getWorktreeTags('wb/1', 'wt 2')).resolves.toEqual(projection)
    await expect(assignWorktreeTag('wb/1', 'wt 2', 'tag/2')).resolves.toBeUndefined()
    await expect(unassignWorktreeTag('wb/1', 'wt 2', 'tag/2')).resolves.toBeUndefined()

    expect(fetchMock.mock.calls[0]?.[0]).toBe('/api/workbenches/wb%2F1/worktrees/wt%202/tags')
    expect(fetchMock.mock.calls[1]?.[0]).toBe('/api/workbenches/wb%2F1/worktrees/wt%202/tags/tag%2F2')
    expect(fetchMock.mock.calls[1]?.[1]).toMatchObject({ method: 'POST' })
    expect(fetchMock.mock.calls[2]?.[0]).toBe('/api/workbenches/wb%2F1/worktrees/wt%202/tags/tag%2F2')
    expect(fetchMock.mock.calls[2]?.[1]).toMatchObject({ method: 'DELETE' })
  })

  it('serializes tag IDs for server-side search and returns projections', async () => {
    const result = { workbenches: [], worktrees: [{ entityType: 'worktree', entityId: 'wt-1', workbenchId: 'wb-1', direct: ['tag-2'], effective: ['tag-1', 'tag-2'], available: true }] }
    const fetchMock = vi.fn(async () => new Response(JSON.stringify(result), { status: 200 }))
    vi.stubGlobal('fetch', fetchMock)

    await expect(searchWorkbenches(['tag/1', 'tag-2'])).resolves.toEqual(result)

    expect(fetchMock.mock.calls[0]?.[0]).toBe('/api/workbenches/search')
    expect(fetchMock.mock.calls[0]?.[1]).toMatchObject({ method: 'POST' })
    expect(JSON.parse(String((fetchMock.mock.calls[0]?.[1] as RequestInit).body))).toEqual({ tagIds: ['tag/1', 'tag-2'] })
  })
})
