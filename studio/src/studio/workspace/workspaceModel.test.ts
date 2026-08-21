// Pure-model tests for workspace geometry. FlexLayout model operations are
// DOM-independent, so the real Model + Actions run fine under happy-dom/node
// and let us verify docking/splitting semantics without rendering.

import { describe, expect, it } from 'vitest'
import { Actions, DockLocation, Model } from 'flexlayout-react'
import { buildDefaultWorkspaceLayout, DEFAULT_WORKSPACE_TABSET_ID } from './defaultLayout'
import {
  DEFAULT_WORKSPACE_VIEW_KINDS,
  workspaceViewInstanceId,
  workspaceViewKindForInstanceId,
} from './workspaceTypes'

const tabsetIds = (model: Model): string[] => {
  const ids: string[] = []
  model.visitNodes(node => {
    if (node.getType() === 'tabset') ids.push(node.getId())
  })
  return ids
}

const tabIdsOf = (model: Model, tabsetId: string): string[] => {
  const tabset = model.getNodeById(tabsetId)
  return tabset ? tabset.getChildren().map(child => child.getId()) : []
}

describe('workspace model geometry', () => {
  it('starts as one tabset with the four view tabs in kind order', () => {
    const model = Model.fromJson(buildDefaultWorkspaceLayout())

    expect(tabsetIds(model)).toEqual([DEFAULT_WORKSPACE_TABSET_ID])
    expect(tabIdsOf(model, DEFAULT_WORKSPACE_TABSET_ID))
      .toEqual(DEFAULT_WORKSPACE_VIEW_KINDS.map(workspaceViewInstanceId))
  })

  it('moving a tab to a tabset edge splits it into a second tabset', () => {
    const model = Model.fromJson(buildDefaultWorkspaceLayout())

    model.doAction(Actions.moveNode(
      workspaceViewInstanceId('source'),
      DEFAULT_WORKSPACE_TABSET_ID,
      DockLocation.RIGHT,
      -1,
      true,
    ))

    const tabsets = tabsetIds(model)
    expect(tabsets).toHaveLength(2)

    const sourceTabset = tabsets.find(id =>
      tabIdsOf(model, id).includes(workspaceViewInstanceId('source')))
    expect(sourceTabset).toBeDefined()
    expect(tabIdsOf(model, sourceTabset!)).toEqual([workspaceViewInstanceId('source')])
    expect(tabIdsOf(model, DEFAULT_WORKSPACE_TABSET_ID)).toEqual([
      workspaceViewInstanceId('overview'),
      workspaceViewInstanceId('chat'),
      workspaceViewInstanceId('knowledge'),
    ])
  })

  it('selectTab keeps the instanceId ↔ kind mapping intact for onTabChange', () => {
    const model = Model.fromJson(buildDefaultWorkspaceLayout())

    model.doAction(Actions.selectTab(workspaceViewInstanceId('knowledge')))

    const selectedId = model.getActiveTabset()?.getSelectedNode()?.getId()
    expect(selectedId).toBe(workspaceViewInstanceId('knowledge'))
    // This is the exact mapping WorkspaceHost's onModelChange handler applies.
    expect(workspaceViewKindForInstanceId(selectedId!)).toBe('knowledge')
  })
})

describe('workspaceViewKindForInstanceId', () => {
  it('round-trips every workspace view kind', () => {
    for (const kind of DEFAULT_WORKSPACE_VIEW_KINDS) {
      expect(workspaceViewKindForInstanceId(workspaceViewInstanceId(kind))).toBe(kind)
    }
  })

  it('returns null for ids that are not workspace view instances', () => {
    expect(workspaceViewKindForInstanceId('')).toBeNull()
    expect(workspaceViewKindForInstanceId('view:unknown')).toBeNull()
    expect(workspaceViewKindForInstanceId('tabset:workspace')).toBeNull()
  })
})
