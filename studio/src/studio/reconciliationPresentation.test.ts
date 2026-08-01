import { describe, expect, it } from 'vitest'
import type { ReconciliationEntry } from '@/api/client'
import {
  actionableEntries,
  comparedEntries,
  comparisonState,
  toggleApprovedPath,
} from './reconciliationPresentation'

const entry = (
  kind: ReconciliationEntry['kind'],
  fingerprintsMatch: boolean | null,
  baselineHash: string | null = 'stored-hash',
  stagingHash: string | null = 'live-hash',
): ReconciliationEntry => ({
  relativePath: 'Blocks/Main.xml',
  kind,
  baselineHash,
  stagingHash,
  componentIdentity: 'main',
  storedFingerprints: fingerprintsMatch === null ? null : 'stored',
  liveFingerprints: fingerprintsMatch === null ? null : fingerprintsMatch ? 'stored' : 'live',
  fingerprintsMatch,
})

describe('TIA comparison presentation', () => {
  it.each([
    ['Added', null, null, 'live-hash', 'new'],
    ['Removed', null, 'stored-hash', null, 'missing'],
    ['Changed', null, 'stored-hash', 'live-hash', 'different'],
    ['Unchanged', true, 'same-hash', 'same-hash', 'same'],
    ['Unchanged', false, 'same-hash', 'same-hash', 'different'],
    ['Unchanged', null, 'same-hash', 'same-hash', 'same'],
    ['Unchanged', null, null, null, 'unverifiable'],
  ] as const)(
    'maps %s with fingerprint match %s to %s',
    (kind, match, baselineHash, stagingHash, expected) => {
      expect(comparisonState(entry(kind, match, baselineHash, stagingHash))).toBe(expected)
    },
  )

  it('tracks explicit selection for every actionable state', () => {
    const entries = [
      entry('Added', null, null, 'new'),
      { ...entry('Changed', false), relativePath: 'Blocks/Changed.xml' },
      { ...entry('Removed', null, 'old', null), relativePath: 'Blocks/Removed.xml' },
      { ...entry('Unchanged', true, 'same', 'same'), relativePath: 'Blocks/Same.xml' },
    ]

    expect(actionableEntries(entries).map(value => value.relativePath)).toEqual([
      'Blocks/Main.xml',
      'Blocks/Changed.xml',
      'Blocks/Removed.xml',
    ])
    const selected = toggleApprovedPath(new Set<string>(), 'Blocks/Changed.xml', true)
    expect([...selected]).toEqual(['Blocks/Changed.xml'])
    expect(toggleApprovedPath(selected, 'Blocks/Changed.xml', false).size).toBe(0)
  })

  it('hides unchanged entries unless a genuine fingerprint mismatch exists', () => {
    const entries = [
      entry('Added', null, null, 'new'),
      { ...entry('Changed', false), relativePath: 'Blocks/Changed.xml' },
      { ...entry('Unchanged', true, 'same', 'same'), relativePath: 'Blocks/Same.xml' },
      // Tag tables carry no fingerprints: equal hashes must not earn a row.
      { ...entry('Unchanged', null, 'same', 'same'), relativePath: 'TagTables/Tags.xml' },
      // Fingerprint moved but exported content did not — worth surfacing.
      { ...entry('Unchanged', false, 'same', 'same'), relativePath: 'Blocks/Drifted.xml' },
    ]

    expect(comparedEntries(entries).map(value => value.relativePath)).toEqual([
      'Blocks/Main.xml',
      'Blocks/Changed.xml',
      'Blocks/Drifted.xml',
    ])
  })

  it('uses content evidence when one or both fingerprint sides are unavailable', () => {
    const oneMissing = {
      ...entry('Unchanged', null, 'same-hash', 'same-hash'),
      storedFingerprints: 'stored',
      liveFingerprints: null,
    }
    const bothMissing = {
      ...oneMissing,
      storedFingerprints: null,
    }

    expect(comparisonState(oneMissing)).toBe('same')
    expect(comparisonState(bothMissing)).toBe('same')
  })
})
