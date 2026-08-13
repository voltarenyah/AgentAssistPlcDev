import { readFileSync } from 'node:fs'
import { describe, expect, it } from 'vitest'

const source = readFileSync(new URL('./MainStudio.tsx', import.meta.url), 'utf8')

const functionBody = (name: string, nextName: string) => {
  const start = source.indexOf(`const ${name} = async`)
  const end = source.indexOf(`const ${nextName} = async`, start)
  expect(start).toBeGreaterThanOrEqual(0)
  expect(end).toBeGreaterThan(start)
  return source.slice(start, end)
}

describe('MainStudio offline snapshot contract', () => {
  it('synchronizes the ApiHost project selection before opening the assistant context', () => {
    const body = functionBody('selectWorkbench', 'selectWorktree')
    expect(body).toContain('await api.selectWorkbench(workbench.workbenchId)')
  })

  it('refreshes the shared runtime snapshot after the selected workbench is synchronized', () => {
    const body = functionBody('selectWorkbench', 'selectWorktree')
    expect(body).toContain('api.getAppAssistantRuntimeState(workbench.workbenchId)')
  })

  it('synchronizes the ApiHost worktree selection before loading worktree context', () => {
    const body = functionBody('selectWorktree', 'selectDevice')
    expect(body).toContain('await api.selectWorktree(workbench.workbenchId, worktree.worktreeId)')
  })

  it('refreshes the shared runtime snapshot after the selected worktree is synchronized', () => {
    const body = functionBody('selectWorktree', 'selectDevice')
    expect(body).toContain('api.getAppAssistantRuntimeState(workbench.workbenchId)')
  })

  it('remounts the assistant when the selected project changes', () => {
    expect(source).toContain('<AppAssistantPanel')
    expect(source).toContain('key={selection.workbenchId}')
  })

  it('refreshes the shared runtime snapshot after the selected device is synchronized', () => {
    const body = functionBody('selectDevice', 'createWorkbench')
    expect(body).toContain('api.getAppAssistantRuntimeState(workbench.workbenchId)')
  })

  it('synchronizes the ApiHost device focus before loading device context', () => {
    const body = functionBody('selectDevice', 'createWorkbench')
    expect(body).toContain('api.getDeviceInfo(workbench.workbenchId, worktree.worktreeId, deviceId)')
    expect(body).toContain('api.selectDevice(workbench.workbenchId, worktree.worktreeId, deviceId)')
    expect(body).not.toContain('api.getBlocks')
    expect(body).not.toContain('api.getVcStatus')
  })

  it('passes explicit workbench, worktree, and device identity to every device workflow', () => {
    expect(source).not.toContain('api.getSelectedDeviceInfo(')
    expect(source).not.toContain('api.stageDeviceRefresh(selection.deviceId')
    expect(source).not.toContain('api.previewDeviceRefresh(selection.deviceId')
    expect(source).not.toContain('api.applyDeviceRefresh(selection.deviceId')
    expect(source).not.toContain('api.updateDeviceKnowledge(selection.deviceId')
    expect(source).not.toContain('api.rebuildDeviceKnowledge(selection.deviceId')
    expect(source).not.toContain('api.prepareDeviceEdit(selection.deviceId')
    expect(source).not.toContain('api.importDeviceSource(selection.deviceId')
    expect(source).not.toContain('api.listDeviceSessions(deviceId)')
    expect(source).not.toContain('api.mergeWorktree(activeWorktree.worktreeId')
  })

  it('clears the previous import result when the selected context changes', () => {
    expect(source).toContain('setLastImport(null)')
  })

  it.each([
    ['applyRefresh', 'updateKnowledge'],
    ['updateKnowledge', 'prepareEdit'],
    ['prepareEdit', 'importSource'],
    ['importSource', 'mergeIntoMaster'],
  ])('reloads the persisted snapshot after %s', (name, nextName) => {
    expect(functionBody(name, nextName)).toContain('await reloadDeviceSnapshot(context)')
  })

  it('shows compile approval before retrying a failed stage refresh with automatic compile', () => {
    const body = functionBody('stageRefresh', 'openProjectInTia')
    expect(body).toContain('allowCompile')
    expect(body).toContain('PLC_COMPILE_REQUIRED')
    expect(body).toContain('setCompilePrompt')
  })

  it('shows progress and operation details while creating a linked worktree', () => {
    expect(source).toContain('Creating linked worktree…')
    expect(source).toContain('data-creation-progress')
    expect(source).toContain('operationStatus={activeOperation?.kind === \'create-worktree\' ? activeOperation.status : null}')
  })
})
