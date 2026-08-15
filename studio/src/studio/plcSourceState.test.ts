import { describe, expect, it } from 'vitest'
import type { OfflineBlockInfo, SourceObjectInfo } from '@/api/client'
import {
  blocksToSourceObjects,
  countSourceObjectsByType,
  filterSourceObjects,
  resolveSourceObjects,
  sourceContextPrefix,
} from './plcSourceState'

const item = (patch: Partial<SourceObjectInfo>): SourceObjectInfo => ({
  id: 'id',
  name: 'Unnamed',
  number: null,
  category: 'FB',
  programmingLanguage: 'SCL',
  groupPath: null,
  relativePath: 'Blocks/Unnamed [FB1].xml',
  contentHash: null,
  isKnowHowProtected: null,
  modifiedDate: null,
  status: null,
  ...patch,
})

const items: SourceObjectInfo[] = [
  item({ id: '1', name: 'Main', category: 'OB', number: 1, relativePath: 'Blocks/Main [OB1].xml' }),
  item({ id: '2', name: 'Motor', category: 'FB', number: 10, relativePath: 'Blocks/Motor [FB10].xml', groupPath: 'Drives' }),
  item({ id: '3', name: 'Scale', category: 'FC', number: 2, relativePath: 'Blocks/Scale [FC2].xml' }),
  item({ id: '4', name: 'Settings', category: 'DB', number: 5, relativePath: 'Blocks/Settings [DB5].xml' }),
  item({ id: '5', name: 'Default tag table', category: 'Tags', relativePath: 'Tags/Default tag table.xml' }),
  item({ id: '6', name: 'AxisData', category: 'UDT', relativePath: 'Types/AxisData.xml' }),
]

describe('filterSourceObjects', () => {
  it('returns every item for the all filter without a query', () => {
    expect(filterSourceObjects(items, 'all', '')).toHaveLength(6)
  })

  it('keeps only items of the selected category', () => {
    const result = filterSourceObjects(items, 'FB', '')
    expect(result).toHaveLength(1)
    expect(result[0].name).toBe('Motor')
  })

  it('filters tag tables and UDTs by their category', () => {
    expect(filterSourceObjects(items, 'Tags', '').map(entry => entry.name)).toEqual(['Default tag table'])
    expect(filterSourceObjects(items, 'UDT', '').map(entry => entry.name)).toEqual(['AxisData'])
  })

  it('matches the query against name, path, and type with number', () => {
    expect(filterSourceObjects(items, 'all', 'motor')).toHaveLength(1)
    expect(filterSourceObjects(items, 'all', 'settings [db5]')).toHaveLength(1)
    expect(filterSourceObjects(items, 'all', 'OB1').map(entry => entry.name)).toEqual(['Main'])
  })

  it('combines the type filter with the query', () => {
    expect(filterSourceObjects(items, 'DB', 'ob1')).toHaveLength(0)
    expect(filterSourceObjects(items, 'OB', 'main')).toHaveLength(1)
  })

  it('ignores surrounding whitespace in the query', () => {
    expect(filterSourceObjects(items, 'all', '  axisdata  ')).toHaveLength(1)
  })
})

describe('countSourceObjectsByType', () => {
  it('counts items per category plus the total', () => {
    expect(countSourceObjectsByType(items)).toEqual({
      all: 6,
      OB: 1,
      FB: 1,
      FC: 1,
      DB: 1,
      Tags: 1,
      UDT: 1,
    })
  })

  it('returns zeros for an empty list', () => {
    expect(countSourceObjectsByType([])).toEqual({
      all: 0,
      OB: 0,
      FB: 0,
      FC: 0,
      DB: 0,
      Tags: 0,
      UDT: 0,
    })
  })
})

describe('resolveSourceObjects', () => {
  const block: OfflineBlockInfo = {
    id: 'b1',
    name: 'Main',
    number: 1,
    blockType: 'OB',
    programmingLanguage: 'LAD',
    groupPath: null,
    relativePath: 'Blocks/Main [OB1].xml',
    modified: false,
  }

  it('prefers manifest source objects when present', () => {
    const manifest = [item({ id: 'm1', name: 'Motor' })]
    expect(resolveSourceObjects(manifest, [block])).toBe(manifest)
  })

  it('maps blocks into source object rows as a fallback', () => {
    const [row] = resolveSourceObjects([], [block])
    expect(row).toMatchObject({
      id: 'b1',
      name: 'Main',
      number: 1,
      category: 'OB',
      programmingLanguage: 'LAD',
      relativePath: 'Blocks/Main [OB1].xml',
      contentHash: null,
      status: null,
    })
  })

  it('returns an empty list when both sources are empty', () => {
    expect(resolveSourceObjects([], [])).toEqual([])
  })
})

describe('blocksToSourceObjects', () => {
  it('preserves the block identity fields', () => {
    const rows = blocksToSourceObjects([{
      id: 'b2',
      name: 'Scale',
      number: 2,
      blockType: 'FC',
      programmingLanguage: 'SCL',
      groupPath: 'Math',
      relativePath: 'Blocks/Scale [FC2].xml',
      modified: true,
    }])
    expect(rows).toHaveLength(1)
    expect(rows[0].category).toBe('FC')
    expect(rows[0].groupPath).toBe('Math')
    expect(rows[0].isKnowHowProtected).toBeNull()
  })
})

describe('sourceContextPrefix', () => {
  it('includes category, name, number, path, and PLC for numbered blocks', () => {
    expect(sourceContextPrefix({
      name: 'Main',
      category: 'OB',
      number: 1,
      relativePath: 'Blocks/Main [OB1].xml',
      plcName: 'PLC_1',
    })).toBe('[PLC source context: OB "Main" (OB1), path "Blocks/Main [OB1].xml", PLC "PLC_1"]')
  })

  it('omits the number for tag tables and UDTs', () => {
    expect(sourceContextPrefix({
      name: 'Inputs',
      category: 'Tags',
      number: null,
      relativePath: 'Tags/Inputs.xml',
      plcName: 'PLC_1',
    })).toBe('[PLC source context: Tags "Inputs" (Tags), path "Tags/Inputs.xml", PLC "PLC_1"]')
  })
})
