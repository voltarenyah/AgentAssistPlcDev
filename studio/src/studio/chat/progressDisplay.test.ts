import { describe, expect, it } from 'vitest'
import { parseProgressContent, parseProgressLine, progressTitle } from './progressDisplay'

describe('parseProgressLine', () => {
  it('parses round notes as subdued notes', () => {
    expect(parseProgressLine('round 2: calling model')).toEqual({
      kind: 'note',
      text: 'round 2: calling model',
    })
  })

  it('parses usage lines as subdued notes', () => {
    expect(parseProgressLine('usage: 1200 prompt + 80 completion (340 reasoning) tokens')).toEqual({
      kind: 'note',
      text: 'usage: 1200 prompt + 80 completion (340 reasoning) tokens',
    })
  })

  it('parses tool-call lines into name and args', () => {
    expect(parseProgressLine('→ engineering.export_blocks({"deviceId":"plc1"})')).toEqual({
      kind: 'tool-call',
      name: 'engineering.export_blocks',
      args: '{"deviceId":"plc1"}',
    })
  })

  it('parses tool calls with empty args', () => {
    expect(parseProgressLine('→ knowledge.query()')).toEqual({
      kind: 'tool-call',
      name: 'knowledge.query',
      args: '',
    })
  })

  it('keeps parentheses inside args', () => {
    expect(parseProgressLine('→ tool.name({"path":"Blocks/Main [OB1].xml (copy)"})')).toEqual({
      kind: 'tool-call',
      name: 'tool.name',
      args: '{"path":"Blocks/Main [OB1].xml (copy)"}',
    })
  })

  it('parses tool failure lines', () => {
    expect(parseProgressLine('  ✗ engineering.export_blocks: EXPORT_FAILED — TIA busy')).toEqual({
      kind: 'tool-error',
      name: 'engineering.export_blocks',
      message: 'EXPORT_FAILED — TIA busy',
      denied: false,
    })
  })

  it('parses sandbox-denied lines', () => {
    expect(parseProgressLine('  ⛔ knowledge.query: denied by sandbox policy')).toEqual({
      kind: 'tool-error',
      name: 'knowledge.query',
      message: 'denied by sandbox policy',
      denied: true,
    })
  })

  it('treats unrecognized lines as notes', () => {
    expect(parseProgressLine('Error: connection lost')).toEqual({
      kind: 'note',
      text: 'Error: connection lost',
    })
    expect(parseProgressLine('-> get_block({})')).toEqual({
      kind: 'note',
      text: '-> get_block({})',
    })
  })
})

describe('parseProgressContent', () => {
  it('splits multi-line content and drops blank lines', () => {
    const entries = parseProgressContent('round 1: calling model\n\n→ tool.a({})\n  ✗ tool.a: boom')
    expect(entries.map(entry => entry.kind)).toEqual(['note', 'tool-call', 'tool-error'])
  })
})

describe('progressTitle', () => {
  it('uses the first tool name when present', () => {
    expect(progressTitle(parseProgressContent('round 1: calling model\n→ engineering.export_blocks({})')))
      .toBe('engineering.export_blocks')
  })

  it('falls back to Progress for note-only content', () => {
    expect(progressTitle(parseProgressContent('round 1: calling model'))).toBe('Progress')
    expect(progressTitle([])).toBe('Progress')
  })
})
