// Versioned localStorage persistence for the workspace FlexLayout model,
// modeled on shellLayout.ts. Geometry only — the model json carries tab ids
// (view instanceIds), titles and sizes, never domain data. Anything malformed,
// stale, or referencing unknown view ids falls back to null (caller uses the
// default layout).

import type { IJsonModel } from 'flexlayout-react'
import { workspaceViewKindForInstanceId } from './workspaceTypes'

export const WORKSPACE_LAYOUT_STORAGE_KEY = 'plc-studio.workspace-layout.v1'

type WorkspaceLayoutEnvelope = {
  version: 1
  layout: IJsonModel
}

type JsonNode = {
  type?: unknown
  id?: unknown
  children?: unknown
}

const collectTabIds = (node: JsonNode, acc: string[]): void => {
  if (node.type === 'tab') acc.push(typeof node.id === 'string' ? node.id : '')
  if (Array.isArray(node.children)) {
    for (const child of node.children) collectTabIds(child as JsonNode, acc)
  }
}

export const readWorkspaceLayout = (storage: Storage | null): IJsonModel | null => {
  if (!storage) return null
  try {
    const raw = storage.getItem(WORKSPACE_LAYOUT_STORAGE_KEY)
    if (!raw) return null
    const parsed = JSON.parse(raw) as Partial<WorkspaceLayoutEnvelope>
    if (parsed.version !== 1) return null
    const layout = parsed.layout
    if (!layout || typeof layout !== 'object') return null
    if (!layout.global || typeof layout.global !== 'object') return null
    if (!Array.isArray(layout.borders)) return null
    if (!layout.layout || layout.layout.type !== 'row' || !Array.isArray(layout.layout.children)) return null
    const tabIds: string[] = []
    collectTabIds(layout.layout, tabIds)
    for (const id of tabIds) {
      if (!workspaceViewKindForInstanceId(id)) return null
    }
    return layout
  } catch {
    return null
  }
}

export const writeWorkspaceLayout = (storage: Storage | null, layout: IJsonModel): void => {
  if (!storage) return
  try {
    storage.setItem(WORKSPACE_LAYOUT_STORAGE_KEY, JSON.stringify({ version: 1, layout }))
  } catch {
    // storage is optional (quota, privacy mode, test environments)
  }
}
