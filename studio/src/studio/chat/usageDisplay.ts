// Formats the exact per-turn token usage reported by the backend
// (AgentLoop.RoundUsages → ApiHost meta SSE event / session roundUsages)
// for the context-size indicator in the chat composer.

import type { ChatUsage } from '@/api/client'

/** Fallback denominator when the backend does not report a context window. */
export const DEFAULT_CONTEXT_WINDOW = 128_000

/** The last billed round's usage — its promptTokens is the exact current context size. */
export const lastUsageOf = (roundUsages: (ChatUsage | null)[] | undefined): ChatUsage | null => {
  if (!roundUsages) return null
  for (let index = roundUsages.length - 1; index >= 0; index -= 1) {
    const usage = roundUsages[index]
    if (usage) return usage
  }
  return null
}

export const formatTokenCount = (count: number): string => {
  if (count < 1000) return String(count)
  const rounded = (count / 1000).toFixed(1)
  return `${rounded.endsWith('.0') ? rounded.slice(0, -2) : rounded}k`
}

/** e.g. "context: 22.7k / 128k (cache: 20.0k hit / 2.7k miss)" — null when nothing billed yet. */
export const contextLabel = (
  usage: ChatUsage | null | undefined,
  contextWindow: number = DEFAULT_CONTEXT_WINDOW,
): string | null => {
  if (!usage) return null
  const label = `context: ${formatTokenCount(usage.promptTokens)} / ${formatTokenCount(contextWindow)}`
  const hit = usage.promptCacheHitTokens ?? 0
  const miss = usage.promptCacheMissTokens ?? 0
  if (hit === 0 && miss === 0) return label
  return `${label} (cache: ${formatTokenCount(hit)} hit / ${formatTokenCount(miss)} miss)`
}
