import { describe, expect, it } from 'vitest'
import * as api from '@/api/client'
import { countUncommittedSourceObjects, sourceEntry } from './sourceEntries'

const entry = (filePath: string, state = 'Modified'): api.VcStatusEntry => ({ filePath, state } as api.VcStatusEntry)
const status = (entries: api.VcStatusEntry[]): api.VcStatusResult => ({ branch: 'feature/x', entries } as api.VcStatusResult)

describe('sourceEntry', () => {
  it('maps a PLC source object and keeps its device, category and file name', () => {
    expect(sourceEntry(entry('devices/PLC_1/source/Blocks/Main.xml'), 'feature/x')).toMatchObject({
      deviceId: 'PLC_1',
      plcName: 'PLC_1',
      category: 'Block',
      objectName: 'Main',
      state: 'Modified',
      authorizedOnMaster: true,
    })
  })

  it('treats a master edit as unauthorized and an added file as added', () => {
    expect(sourceEntry(entry('devices/PLC_1/source/DB/Data.xml'), 'master')?.state).toBe('Unauthorized')
    expect(sourceEntry(entry('devices/PLC_1/source/DB/Data.xml', 'Untracked'), 'feature/x')?.state).toBe('Added')
  })

  it('maps hardware outside the staging area, and nothing else', () => {
    expect(sourceEntry(entry('hardware/aml/project.aml'), 'feature/x')).toMatchObject({
      deviceId: 'project', plcName: 'Hardware', category: 'Hardware', objectName: 'project.aml',
    })
    expect(sourceEntry(entry('hardware/staging/project.aml'), 'feature/x')).toBeNull()
    expect(sourceEntry(entry('docs/readme.md'), 'feature/x')).toBeNull()
    expect(sourceEntry(entry('devices/PLC_1/source/Blocks/Main.txt'), 'feature/x')).toBeNull()
  })
})

describe('countUncommittedSourceObjects', () => {
  it('counts the source objects and ignores everything else', () => {
    const value = status([
      entry('devices/PLC_1/source/Blocks/Main.xml'),
      entry('devices/PLC_1/source/DB/Data.xml'),
      entry('hardware/aml/project.aml'),
      entry('devices/PLC_1/source/Blocks/Notes.txt'),
      entry('hardware/staging/project.aml'),
    ])
    expect(countUncommittedSourceObjects(value, 'feature/x')).toBe(3)
  })

  it('is zero for a clean or unread worktree', () => {
    expect(countUncommittedSourceObjects(status([]), 'feature/x')).toBe(0)
    expect(countUncommittedSourceObjects(null, 'feature/x')).toBe(0)
  })
})
