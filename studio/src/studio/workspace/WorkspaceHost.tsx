// WorkspaceHost renders the FlexLayout <Layout> for the device workspace. The
// model is owned by the WorkspaceService (semantic navigation lives there);
// this component only provides the factory/render glue and the theme hook.

import { useCallback, useSyncExternalStore } from 'react'
import {
  Layout,
  type ITabRenderValues,
  type TabNode,
} from 'flexlayout-react'
import 'flexlayout-react/style/dark.css'
import WorkbenchViewHost from './WorkbenchViewHost'
import { workspaceViewRegistry } from './ViewRegistry'
import type { WorkspaceService } from './WorkspaceService'
import type {
  WorkbenchViewInstance,
  WorkspaceViewKind,
  WorkspaceViewProps,
} from './workspaceTypes'

export type {
  WorkspaceChatProps,
  WorkspaceGitProps,
  WorkspaceKnowledgeProps,
  WorkspaceSourceProps,
  WorkspaceViewKind,
  WorkspaceViewProps,
} from './workspaceTypes'

export type WorkspaceHostProps = WorkspaceViewProps & {
  workspace: WorkspaceService
}

export default function WorkspaceHost({ workspace, overview, chat, source, knowledge, git }: WorkspaceHostProps) {
  // Re-reads (and remounts the Layout via key) when the service replaces the
  // model instance (resetLayout); stable identity otherwise.
  const { model, version } = useSyncExternalStore(
    callback => workspace.subscribeModel(callback),
    () => workspace.getModelSnapshot(),
  )
  const factory = useCallback((node: TabNode) => {
    const instance: WorkbenchViewInstance = {
      instanceId: node.getId(),
      kind: node.getComponent() as WorkspaceViewKind,
    }
    return (
      <WorkbenchViewHost
        instance={instance}
        viewProps={{ overview, chat, source, knowledge, git }}
      />
    )
  }, [overview, chat, source, knowledge, git])

  const renderTab = useCallback((node: TabNode, renderValues: ITabRenderValues) => {
    const kind = node.getComponent() as WorkspaceViewKind
    const Icon = workspaceViewRegistry[kind]?.icon
    if (Icon) {
      renderValues.leading = <Icon className="h-3 w-3" />
    }
  }, [])

  return (
    <div className="relative min-h-0 flex-1">
      <Layout
        key={version}
        model={model}
        factory={factory}
        onRenderTab={renderTab}
      />
    </div>
  )
}
