import type { TagNode } from '@/api/client'

export function tagPaths(nodes: TagNode[]): Map<string, string> {
  const byId = new Map(nodes.map(node => [node.tagId, node]))
  const paths = new Map<string, string>()
  const visiting = new Set<string>()

  const pathFor = (id: string): string => {
    const cached = paths.get(id)
    if (cached) return cached
    const node = byId.get(id)
    if (!node) return id
    if (visiting.has(id)) return node.name
    visiting.add(id)
    const path = node.parentTagId && byId.has(node.parentTagId)
      ? `${pathFor(node.parentTagId)}/${node.name}`
      : node.name
    visiting.delete(id)
    paths.set(id, path)
    return path
  }

  for (const node of nodes) pathFor(node.tagId)
  return paths
}

export function validTagPath(value: string): string | null {
  const segments = value.split('/').map(segment => segment.trim())
  if (segments.length < 2 || segments.some(segment => segment.length === 0)) return null
  return segments.join('/')
}
