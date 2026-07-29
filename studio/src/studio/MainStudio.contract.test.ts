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
  it('does not request live blocks while selecting a device', () => {
    const body = functionBody('selectDevice', 'createWorkbench')
    expect(body).toContain('api.getSelectedDeviceInfo()')
    expect(body).not.toContain('api.getBlocks')
  })

  it.each([
    ['applyRefresh', 'updateKnowledge'],
    ['updateKnowledge', 'prepareEdit'],
    ['prepareEdit', 'importSource'],
    ['importSource', 'mergeIntoMaster'],
  ])('reloads the persisted snapshot after %s', (name, nextName) => {
    expect(functionBody(name, nextName)).toContain('await reloadDeviceSnapshot()')
  })
})
