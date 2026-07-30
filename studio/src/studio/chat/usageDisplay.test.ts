import { describe, expect, it } from 'vitest'
import { contextLabel, formatTokenCount, lastUsageOf } from './usageDisplay'

describe('usage display', () => {
  it('formats token counts in k units above one thousand', () => {
    expect(formatTokenCount(999)).toBe('999')
    expect(formatTokenCount(22678)).toBe('22.7k')
    expect(formatTokenCount(128000)).toBe('128k')
  })

  it('takes the last non-null round usage as the current context size', () => {
    expect(lastUsageOf(undefined)).toBeNull()
    expect(lastUsageOf([])).toBeNull()
    expect(lastUsageOf([null, null])).toBeNull()
    expect(lastUsageOf([
      { promptTokens: 1000, completionTokens: 10, totalTokens: 1010 },
      null,
      { promptTokens: 22678, completionTokens: 269, totalTokens: 22947 },
    ])?.promptTokens).toBe(22678)
  })

  it('labels context size against the context window', () => {
    expect(contextLabel(null)).toBeNull()
    expect(contextLabel(undefined)).toBeNull()
    expect(contextLabel({ promptTokens: 22678, completionTokens: 269, totalTokens: 22947 }))
      .toBe('context: 22.7k / 128k')
    expect(contextLabel({ promptTokens: 22678, completionTokens: 269, totalTokens: 22947 }, 65536))
      .toBe('context: 22.7k / 65.5k')
  })

  it('includes cache hit and miss only when non-zero', () => {
    expect(contextLabel({
      promptTokens: 22678,
      completionTokens: 269,
      totalTokens: 22947,
      promptCacheHitTokens: 0,
      promptCacheMissTokens: 0,
    })).toBe('context: 22.7k / 128k')
    expect(contextLabel({
      promptTokens: 22678,
      completionTokens: 269,
      totalTokens: 22947,
      promptCacheHitTokens: 20000,
      promptCacheMissTokens: 2678,
    })).toBe('context: 22.7k / 128k (cache: 20k hit / 2.7k miss)')
  })
})
