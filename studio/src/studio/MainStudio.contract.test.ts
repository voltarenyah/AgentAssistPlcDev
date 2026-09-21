import { readFileSync } from 'node:fs'
import { describe, expect, it } from 'vitest'

const source = readFileSync(new URL('./MainStudio.tsx', import.meta.url), 'utf8')

// Selection handlers are not all async: they apply local focus immediately and
// issue the ApiHost acknowledgement in parallel, so match either form.
const functionBody = (name: string, nextName: string) => {
  const start = source.indexOf(`const ${name} =`)
  const end = source.indexOf(`const ${nextName} =`, start)
  expect(start).toBeGreaterThanOrEqual(0)
  expect(end).toBeGreaterThan(start)
  return source.slice(start, end)
}

// The shared runtime snapshot is refreshed by an effect rather than inside the
// selection handlers, so those contract checks read the effect owning the call.
const effectContaining = (marker: string) => {
  const at = source.indexOf(marker)
  expect(at, `effect containing ${marker}`).toBeGreaterThanOrEqual(0)
  const start = source.lastIndexOf('useEffect(', at)
  const end = source.indexOf('])', at)
  expect(start).toBeGreaterThanOrEqual(0)
  expect(end).toBeGreaterThan(start)
  return source.slice(start, end + 2)
}

describe('MainStudio offline snapshot contract', () => {
  it('applies the project selection locally and synchronizes the ApiHost in parallel', () => {
    const body = functionBody('selectWorkbench', 'selectWorktree')
    const localAt = body.indexOf('setSelection(')
    const apiAt = body.indexOf('api.selectWorkbench(workbench.workbenchId)')
    expect(localAt).toBeGreaterThanOrEqual(0)
    expect(apiAt).toBeGreaterThan(localAt)
  })

  it('refreshes the shared runtime snapshot whenever the selected project changes', () => {
    const effect = effectContaining('api.getAppAssistantRuntimeState(selection.workbenchId)')
    expect(effect).toContain('[selection.workbenchId]')
  })

  it('applies the worktree selection locally and synchronizes the ApiHost in parallel', () => {
    const body = functionBody('selectWorktree', 'selectDevice')
    const localAt = body.indexOf('setSelection(')
    const apiAt = body.indexOf('api.selectWorktree(workbench.workbenchId, worktree.worktreeId)')
    expect(localAt).toBeGreaterThanOrEqual(0)
    expect(apiAt).toBeGreaterThan(localAt)
  })

  it('keeps the shared runtime snapshot keyed to the project rather than each worktree', () => {
    const body = functionBody('selectWorktree', 'selectDevice')
    expect(body).not.toContain('api.getAppAssistantRuntimeState')
    const effect = effectContaining('api.getAppAssistantRuntimeState(selection.workbenchId)')
    expect(effect).toContain('[selection.workbenchId]')
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

  it.each([
    ['applyRefresh', 'updateKnowledge'],
    ['updateKnowledge', 'mergeIntoMaster'],
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
