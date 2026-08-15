// Builds the default FlexLayout model JSON: a single tabset holding one tab
// per workspace view. Tab ids are the stable view instanceIds; tabs carry no
// config beyond kind (in `component`) — FlexLayout owns geometry only.

import type { IJsonModel } from 'flexlayout-react'
import { workspaceViewRegistry } from './ViewRegistry'
import { defaultWorkspaceViewInstances } from './workspaceTypes'

export const DEFAULT_WORKSPACE_TABSET_ID = 'tabset:workspace'

export const buildDefaultWorkspaceLayout = (): IJsonModel => ({
  global: {
    // Geometry-only affordances: drag to re-dock/split is the whole point of
    // the workspace; renaming, closing, and popout windows stay off (views
    // are fixed, popouts are out of scope).
    tabEnableDrag: true,
    tabEnableClose: false,
    tabEnableRename: false,
    tabEnablePopout: false,
  },
  borders: [],
  layout: {
    type: 'row',
    children: [
      {
        type: 'tabset',
        id: DEFAULT_WORKSPACE_TABSET_ID,
        children: defaultWorkspaceViewInstances().map(instance => ({
          type: 'tab',
          id: instance.instanceId,
          name: workspaceViewRegistry[instance.kind].title,
          component: instance.kind,
        })),
      },
    ],
  },
})
