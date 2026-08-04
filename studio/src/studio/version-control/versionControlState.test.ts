import { describe, expect, it } from 'vitest'
import type { VcSourceEntry } from '@/api/client'
import {
  groupSourceObjects,
  mergeBlockReason,
  togglePath,
  validationLabel,
} from './versionControlState'

const object = (
  deviceId: string,
  plcName: string,
  category: VcSourceEntry['category'],
  filePath: string,
): VcSourceEntry => ({
  deviceId,
  plcName,
  category,
  filePath,
  objectName: filePath.split('/').at(-1)?.replace(/\.xml$/i, '') ?? filePath,
  state: 'Modified',
  authorizedOnMaster: true,
})

describe('versionControlState', () => {
  it('groups objects by device and PLC category', () => {
    const groups = groupSourceObjects([
      object('dev-2', 'PLC_2', 'Tags', 'Tags/Inputs.xml'),
      object('dev-1', 'PLC_1', 'Block', 'Blocks/Main.xml'),
      object('dev-1', 'PLC_1', 'Udt', 'UDT/Motor.xml'),
    ])

    expect(groups.map(group => group.key)).toEqual([
      'PLC_1/Block',
      'PLC_1/Udt',
      'PLC_2/Tags',
    ])
  })

  it('toggles one repository path without changing other selections', () => {
    expect([...togglePath(new Set(['a.xml']), 'b.xml', true)]).toEqual(['a.xml', 'b.xml'])
    expect([...togglePath(new Set(['a.xml', 'b.xml']), 'a.xml', false)]).toEqual(['b.xml'])
  })

  it('distinguishes validated, unlabeled, and invalid history', () => {
    expect(validationLabel('Validated')).toBe('TIA validated')
    expect(validationLabel('Unlabeled')).toBe('Full scan required')
    expect(validationLabel('Invalid')).toBe('Validation evidence invalid')
  })

  it('returns an item reason without globally blocking unrelated imports', () => {
    expect(mergeBlockReason({ importable: false, reason: 'TIA_FEATURE_OVERLAP' })).toBe(
      'This object changed in both TIA and the feature.',
    )
    expect(mergeBlockReason({ importable: true, reason: null })).toBeNull()
  })
})
