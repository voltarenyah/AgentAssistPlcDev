// Renders one WorkbenchViewInstance by resolving its kind through the view
// registry, wrapped in an error boundary so a single crashing view does not
// kill the whole layout.

import ErrorBoundary from '@/components/ErrorBoundary'
import { workspaceViewRegistry } from './ViewRegistry'
import type { WorkbenchViewInstance, WorkspaceViewProps } from './workspaceTypes'

export type WorkbenchViewHostProps = {
  instance: WorkbenchViewInstance
  viewProps: WorkspaceViewProps
}

export default function WorkbenchViewHost({ instance, viewProps }: WorkbenchViewHostProps) {
  const definition = workspaceViewRegistry[instance.kind]
  return (
    <ErrorBoundary>
      {definition.render(viewProps)}
    </ErrorBoundary>
  )
}
