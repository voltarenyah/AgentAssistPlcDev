import type { ChatSettings, DeepSeekBalance } from '@/api/client'

/* ── Model / effort options (mirror ChatWorkspace) ─────────────────────── */

export const MODEL_OPTIONS = [
  { value: 'deepseek-v4-flash', label: 'Flash' },
  { value: 'deepseek-v4-pro', label: 'Pro' },
]

export const EFFORT_OPTIONS = ['low', 'medium', 'high']

/* ── Chat settings merging ─────────────────────────────────────────────── */

export const mergeChatSettings = (current: ChatSettings, patch: Partial<ChatSettings>): ChatSettings => ({
  ...current,
  ...patch,
})

/* ── Numeric parsing / clamping ────────────────────────────────────────── */

/** Parse an integer settings input; falls back when blank/invalid and clamps to [min, max]. */
export const parseNumberField = (raw: string, fallback: number, min: number, max: number): number => {
  const value = Number(raw)
  if (raw.trim() === '' || !Number.isFinite(value)) return fallback
  return Math.round(Math.max(min, Math.min(max, value)))
}

export const clampUnitInterval = (value: number, min: number, max: number): number =>
  Math.max(min, Math.min(max, Math.round(value * 100) / 100))

/* ── Agent loop (advanced) numeric fields ──────────────────────────────── */

export type AdvancedSettingKey =
  | 'roundLimit'
  | 'promptTokenBudget'
  | 'promptTokenWarningThreshold'
  | 'toolResultMaxChars'
  | 'toolResultCompactChars'
  | 'historyTokenThreshold'
  | 'recentTurnsToKeep'
  | 'collapsedAnswerChars'

export type AdvancedSettingField = {
  key: AdvancedSettingKey
  title: string
  description: string
  min: number
  max: number
}

export const ADVANCED_SETTING_FIELDS: AdvancedSettingField[] = [
  { key: 'roundLimit', title: 'Round limit', description: 'Maximum agent loop rounds before the run stops.', min: 1, max: 200 },
  { key: 'promptTokenBudget', title: 'Prompt token budget', description: 'Token budget for a single prompt after compaction.', min: 1000, max: 1_000_000 },
  { key: 'promptTokenWarningThreshold', title: 'Prompt token warning', description: 'Warn when the prompt exceeds this many tokens.', min: 1000, max: 1_000_000 },
  { key: 'toolResultMaxChars', title: 'Tool result max chars', description: 'Hard cap for a single tool result payload.', min: 100, max: 200_000 },
  { key: 'toolResultCompactChars', title: 'Tool result compact chars', description: 'Tool results above this size are compacted.', min: 100, max: 200_000 },
  { key: 'historyTokenThreshold', title: 'History token threshold', description: 'Conversation history is collapsed past this size.', min: 1000, max: 1_000_000 },
  { key: 'recentTurnsToKeep', title: 'Recent turns to keep', description: 'Turns kept verbatim when history is collapsed.', min: 0, max: 100 },
  { key: 'collapsedAnswerChars', title: 'Collapsed answer chars', description: 'Characters kept from answers in collapsed history.', min: 100, max: 100_000 },
]

/** Fields present in the loaded settings, in display order. */
export const presentAdvancedFields = (settings: ChatSettings | null): AdvancedSettingField[] => {
  if (!settings) return []
  return ADVANCED_SETTING_FIELDS.filter(field => typeof settings[field.key] === 'number')
}

/* ── Category model + sidebar search ───────────────────────────────────── */

export type SettingsCategoryId = 'general' | 'assistant' | 'agent-loop' | 'appearance' | 'about'

export type SettingsIconName = 'gauge' | 'sparkles' | 'bot' | 'palette' | 'info'

export type SettingsCategory = {
  id: SettingsCategoryId
  group: string
  icon: SettingsIconName
  label: string
  description: string
  keywords: string[]
}

export const SETTINGS_CATEGORIES: SettingsCategory[] = [
  {
    id: 'general',
    group: 'Set up',
    icon: 'gauge',
    label: 'General',
    description: 'Workspace defaults, app setup, and maintenance.',
    keywords: ['status', 'server', 'layout', 'workspace', 'reset', 'tia', 'tools'],
  },
  {
    id: 'assistant',
    group: 'AI capabilities',
    icon: 'sparkles',
    label: 'Assistant',
    description: 'DeepSeek credentials, account balance, and generation defaults.',
    keywords: ['api key', 'deepseek', 'model', 'thinking', 'reasoning', 'temperature', 'top p', 'balance'],
  },
  {
    id: 'agent-loop',
    group: 'AI capabilities',
    icon: 'bot',
    label: 'Agent Loop',
    description: 'Advanced agent-loop budgets and limits.',
    keywords: ['advanced', 'tokens', 'budget', 'truncation', 'loop', 'history', 'rounds'],
  },
  {
    id: 'appearance',
    group: 'Interface',
    icon: 'palette',
    label: 'Appearance',
    description: 'Theme and visual preferences.',
    keywords: ['theme', 'dark', 'light', 'color'],
  },
  {
    id: 'about',
    group: 'Interface',
    icon: 'info',
    label: 'About',
    description: 'Runtime and service information.',
    keywords: ['about', 'runtime', 'sidecar', 'endpoint', 'version', 'origin'],
  },
]

/** Sidebar search: matches a category when the query hits its label, group, description, or keywords. */
export const categoryMatches = (category: SettingsCategory, query: string): boolean => {
  const needle = query.trim().toLowerCase()
  if (!needle) return true
  return [category.label, category.group, category.description, ...category.keywords]
    .join(' ')
    .toLowerCase()
    .includes(needle)
}

export const filterCategories = (query: string): SettingsCategory[] =>
  SETTINGS_CATEGORIES.filter(category => categoryMatches(category, query))

/* ── DeepSeek balance ──────────────────────────────────────────────────── */

export const formatBalance = (balance: DeepSeekBalance | null): string => {
  if (!balance || !balance.isAvailable) return 'Unavailable'
  if (balance.balances.length === 0) return '—'
  return balance.balances
    .map(entry => `${entry.currency === 'USD' ? '$' : `${entry.currency} `}${entry.totalBalance}`)
    .join(' · ')
}

/* ── Sidecar health ────────────────────────────────────────────────────── */

export type SidecarHealth = {
  model: string
  mode: string
}

export const parseSidecarHealth = (body: unknown): SidecarHealth | null => {
  if (!body || typeof body !== 'object') return null
  const record = body as Record<string, unknown>
  if (typeof record.model !== 'string' || typeof record.modelMode !== 'string') return null
  return { model: record.model, mode: record.modelMode }
}
