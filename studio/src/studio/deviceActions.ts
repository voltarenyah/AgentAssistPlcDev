export type DeviceOperationContext = {
  workbenchId: string
  worktreeId: string
  deviceId: string
  operationId: string
}

export const runOpenProjectInTia = (
  open: (
    workbenchId: string,
    worktreeId: string,
    deviceId: string,
    operationId: string,
  ) => Promise<unknown>,
  context: DeviceOperationContext,
) => open(
  context.workbenchId,
  context.worktreeId,
  context.deviceId,
  context.operationId,
)
