import { describe, expect, it } from 'vitest'
import {
  SETTINGS_CATEGORIES,
  categoryMatches,
  clampUnitInterval,
  filterCategories,
  formatBalance,
  parseNumberField,
} from './settingsState'

describe('settingsState', () => {
  it('lists every category with a unique id', () => {
    const ids = SETTINGS_CATEGORIES.map(category => category.id)
    expect(new Set(ids).size).toBe(ids.length)
    expect(ids).toEqual(['general', 'assistant', 'agent-loop', 'appearance', 'about'])
  })

  it('matches categories by label, group, or description', () => {
    const assistant = SETTINGS_CATEGORIES.find(category => category.id === 'assistant')!
    expect(categoryMatches(assistant, '')).toBe(true)
    expect(categoryMatches(assistant, 'deepseek')).toBe(true)
    expect(categoryMatches(assistant, 'AI cap')).toBe(true)
    expect(categoryMatches(assistant, 'zzzz')).toBe(false)
  })

  it('filters the sidebar categories by query', () => {
    expect(filterCategories('')).toHaveLength(SETTINGS_CATEGORIES.length)
    const matches = filterCategories('theme')
    expect(matches.map(category => category.id)).toEqual(['appearance'])
    expect(filterCategories('nothing-matches-this')).toHaveLength(0)
  })

  it('parses numeric fields with fallback and clamping', () => {
    expect(parseNumberField('42', 8, 1, 100)).toBe(42)
    expect(parseNumberField('', 8, 1, 100)).toBe(8)
    expect(parseNumberField('abc', 8, 1, 100)).toBe(8)
    expect(parseNumberField('0', 8, 1, 100)).toBe(1)
    expect(parseNumberField('9999', 8, 1, 100)).toBe(100)
    expect(parseNumberField('7.6', 8, 1, 100)).toBe(8)
  })

  it('clamps unit-interval values to two decimals', () => {
    expect(clampUnitInterval(1.234, 0, 2)).toBe(1.23)
    expect(clampUnitInterval(-1, 0, 2)).toBe(0)
    expect(clampUnitInterval(5, 0, 2)).toBe(2)
  })

  it('formats DeepSeek balances', () => {
    expect(formatBalance(null)).toBe('Unavailable')
    expect(formatBalance({ isAvailable: false, balances: [], fetchedAt: '' })).toBe('Unavailable')
    expect(formatBalance({ isAvailable: true, balances: [], fetchedAt: '' })).toBe('—')
    expect(formatBalance({
      isAvailable: true,
      fetchedAt: '',
      balances: [
        { currency: 'USD', totalBalance: '12.50', grantedBalance: '0', toppedUpBalance: '12.50' },
        { currency: 'CNY', totalBalance: '3.00', grantedBalance: '3.00', toppedUpBalance: '0' },
      ],
    })).toBe('$12.50 · CNY 3.00')
  })
})
