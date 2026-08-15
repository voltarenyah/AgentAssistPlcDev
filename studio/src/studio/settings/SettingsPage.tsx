import { useEffect, useMemo, useRef, useState, type ReactNode } from 'react'
import { ArrowLeft, Bot, Gauge, Info, Palette, RefreshCw, Search, Sparkles } from 'lucide-react'
import * as api from '@/api/client'
import { showErrorToast } from '@/components/ui/toast'
import { Slider } from '@/components/ui/slider'
import { Switch } from '@/components/ui/switch'
import { getThemePreference, setThemePreference, subscribeTheme, type ThemeMode } from '@/studio/theme'
import {
  EFFORT_OPTIONS,
  MODEL_OPTIONS,
  clampUnitInterval,
  filterCategories,
  formatBalance,
  mergeChatSettings,
  parseNumberField,
  parseSidecarHealth,
  presentAdvancedFields,
  type SettingsCategory,
  type SettingsCategoryId,
  type SettingsIconName,
  type SidecarHealth,
} from './settingsState'

const iconMap: Record<SettingsIconName, typeof Gauge> = {
  gauge: Gauge,
  sparkles: Sparkles,
  bot: Bot,
  palette: Palette,
  info: Info,
}

type SidecarState =
  | { state: 'loading' }
  | { state: 'ok'; health: SidecarHealth }
  | { state: 'unreachable' }

const errorMessage = (error: unknown, fallback: string) =>
  error instanceof Error ? error.message : fallback

function Section({ title, subtitle, children }: { title: string; subtitle?: string; children: ReactNode }) {
  return (
    <section className="overflow-hidden rounded-xl border bg-card" style={{ borderColor: 'var(--border)' }}>
      <div className="border-b px-5 py-4" style={{ borderColor: 'var(--border)' }}>
        <h2 className="text-sm font-semibold">{title}</h2>
        {subtitle && <p className="mt-1 text-[10px] text-muted-foreground">{subtitle}</p>}
      </div>
      <div className="divide-y px-5" style={{ borderColor: 'var(--border)' }}>{children}</div>
    </section>
  )
}

function Row({ id, title, description, children }: { id: string; title: string; description: string; children: ReactNode }) {
  return (
    <div data-setting-row={id} className="flex flex-wrap items-center justify-between gap-3 py-3.5">
      <div className="min-w-0 max-w-[420px]">
        <div className="text-[11px] font-medium">{title}</div>
        <div className="mt-0.5 text-[10px] leading-relaxed text-muted-foreground">{description}</div>
      </div>
      <div className="shrink-0">{children}</div>
    </div>
  )
}

const readOnlyValue = (value: string) => <span className="text-[11px] text-muted-foreground">{value}</span>

type Props = {
  onClose: () => void
  onResetLayout?: () => void
}

export default function SettingsPage({ onClose, onResetLayout }: Props) {
  const [settings, setSettings] = useState<api.ChatSettings | null>(null)
  const settingsRef = useRef<api.ChatSettings | null>(null)
  const [status, setStatus] = useState<api.ServerStatus | null>(null)
  const [tiaSessions, setTiaSessions] = useState<api.SessionInfo[] | null>(null)
  const [toolCount, setToolCount] = useState<number | null>(null)
  const [keyConfigured, setKeyConfigured] = useState<boolean | null>(null)
  const [apiKeyDraft, setApiKeyDraft] = useState('')
  const [apiKeySaving, setApiKeySaving] = useState(false)
  const [balance, setBalance] = useState<api.DeepSeekBalance | null>(null)
  const [balanceBusy, setBalanceBusy] = useState(false)
  const [balanceError, setBalanceError] = useState<string | null>(null)
  const [sidecar, setSidecar] = useState<SidecarState>({ state: 'loading' })
  const [theme, setTheme] = useState<ThemeMode>(() => getThemePreference())
  const [query, setQuery] = useState('')
  const [activeCategory, setActiveCategory] = useState<SettingsCategoryId>('general')

  useEffect(() => {
    let cancelled = false
    api.getChatSettings()
      .then(loaded => {
        if (cancelled) return
        settingsRef.current = loaded
        setSettings(loaded)
      })
      .catch(error => showErrorToast(`Could not load chat settings: ${errorMessage(error, 'request failed')}`))
    api.getKeyStatus()
      .then(keyStatus => { if (!cancelled) setKeyConfigured(keyStatus.configured) })
      .catch(() => { if (!cancelled) setKeyConfigured(null) })
    api.getStatus()
      .then(serverStatus => { if (!cancelled) setStatus(serverStatus) })
      .catch(() => { if (!cancelled) setStatus(null) })
    api.getSessions()
      .then(sessions => { if (!cancelled) setTiaSessions(sessions) })
      .catch(() => { if (!cancelled) setTiaSessions(null) })
    api.getTools()
      .then(tools => { if (!cancelled) setToolCount(tools.length) })
      .catch(() => { if (!cancelled) setToolCount(null) })
    api.getAppAssistantHealth()
      .then(body => parseSidecarHealth(body))
      .then(health => {
        if (cancelled) return
        setSidecar(health ? { state: 'ok', health } : { state: 'unreachable' })
      })
      .catch(() => { if (!cancelled) setSidecar({ state: 'unreachable' }) })
    return () => { cancelled = true }
  }, [])

  useEffect(() => subscribeTheme(setTheme), [])

  const changeSettings = (patch: Partial<api.ChatSettings>) => {
    const base = settingsRef.current
    if (!base) return
    const next = mergeChatSettings(base, patch)
    settingsRef.current = next
    setSettings(next)
    api.saveChatSettings(next)
      .catch(error => showErrorToast(`Could not save chat settings: ${errorMessage(error, 'request failed')}`))
  }

  const saveKey = () => {
    const key = apiKeyDraft.trim()
    if (!key || apiKeySaving) return
    setApiKeySaving(true)
    api.saveApiKey(key)
      .then(() => {
        setKeyConfigured(true)
        setApiKeyDraft('')
      })
      .catch(error => showErrorToast(`Could not save the API key: ${errorMessage(error, 'request failed')}`))
      .finally(() => setApiKeySaving(false))
  }

  const refreshBalance = () => {
    if (balanceBusy) return
    setBalanceBusy(true)
    setBalanceError(null)
    api.getDeepSeekBalance()
      .then(setBalance)
      .catch(error => setBalanceError(errorMessage(error, 'Balance unavailable')))
      .finally(() => setBalanceBusy(false))
  }

  const categories = useMemo(() => filterCategories(query), [query])
  const groups = useMemo(() => {
    const names = [...new Set(categories.map(category => category.group))]
    return names.map(name => ({ name, categories: categories.filter(category => category.group === name) }))
  }, [categories])

  const searching = query.trim() !== ''
  const active = categories.find(category => category.id === activeCategory) ?? categories[0] ?? null
  const visibleCategories = searching ? categories : active ? [active] : []

  const decimalControl = (label: string, value: number, min: number, max: number, onCommit: (value: number) => void) => (
    <div className="flex items-center gap-3">
      <Slider
        aria-label={`${label} slider`}
        className="w-36"
        min={min}
        max={max}
        step={0.1}
        value={[value]}
        disabled={!settings}
        onValueChange={values => onCommit(clampUnitInterval(values[0] ?? 0, min, max))}
      />
      <input
        type="number"
        aria-label={label}
        className="field-input h-8 w-20 px-2 text-[11px]"
        min={min}
        max={max}
        step={0.1}
        value={value}
        disabled={!settings}
        onChange={event => {
          if (event.target.value.trim() === '') return
          const parsed = Number(event.target.value)
          if (Number.isFinite(parsed)) onCommit(clampUnitInterval(parsed, min, max))
        }}
      />
    </div>
  )

  const renderCategory = (category: SettingsCategory) => {
    switch (category.id) {
      case 'general':
        return (
          <>
            <Section title="Application status" subtitle="Live view of the local API host.">
              <Row id="general.api-status" title="API server" description="API host reachability and build version.">
                {readOnlyValue(status ? `Online · v${status.version}` : 'Unavailable')}
              </Row>
              <Row id="general.tia-sessions" title="TIA sessions" description="Running TIA Portal sessions and their open project, when any.">
                {readOnlyValue(tiaSessions
                  ? `${tiaSessions.length} session${tiaSessions.length === 1 ? '' : 's'}${tiaSessions.find(session => session.projectPath)?.projectPath ? ` · ${tiaSessions.find(session => session.projectPath)!.projectPath}` : ''}`
                  : 'Unavailable')}
              </Row>
              <Row id="general.mcp-tools" title="Exposed tools" description="Tools currently published to the assistant.">
                {readOnlyValue(toolCount !== null ? `${toolCount} MCP tools` : 'Unavailable')}
              </Row>
            </Section>
            <Section title="Shell layout" subtitle="Dock sizes and visibility for the studio shell.">
              <Row id="general.reset-layout" title="Reset shell layout" description="Restore the default dock layout. Applies immediately.">
                <button className="secondary-button h-8" data-reset-layout onClick={() => onResetLayout?.()}>Reset layout</button>
              </Row>
            </Section>
          </>
        )
      case 'assistant': {
        const knownModel = Boolean(settings && MODEL_OPTIONS.some(option => option.value === settings.model))
        return (
          <Section title={category.label} subtitle={category.description}>
            <Row id="assistant.api-key" title="API key" description="DeepSeek API key used by the assistant backend.">
              <div className="flex flex-wrap items-center justify-end gap-2">
                <span className={`text-[10px] ${keyConfigured ? 'text-emerald-500' : 'text-muted-foreground'}`}>
                  {keyConfigured === null ? 'Checking…' : keyConfigured ? 'Configured' : 'Not configured'}
                </span>
                <input
                  type="password"
                  aria-label="DeepSeek API key"
                  className="field-input h-8 w-48 px-2 text-[11px]"
                  placeholder="sk-…"
                  value={apiKeyDraft}
                  onChange={event => setApiKeyDraft(event.target.value)}
                />
                <button className="primary-button h-8" disabled={apiKeySaving || !apiKeyDraft.trim()} onClick={saveKey}>Save key</button>
              </div>
            </Row>
            <Row id="assistant.balance" title="Account balance" description="Current DeepSeek balance, fetched on demand.">
              <div className="flex flex-wrap items-center justify-end gap-2">
                {readOnlyValue(balanceError ?? (balance ? formatBalance(balance) : keyConfigured === false ? 'Configure an API key first' : 'Not fetched'))}
                <button
                  className="icon-button h-8 w-8"
                  aria-label="Refresh balance"
                  title="Refresh balance"
                  disabled={balanceBusy}
                  onClick={refreshBalance}
                >
                  <RefreshCw className={`h-3.5 w-3.5 ${balanceBusy ? 'animate-spin' : ''}`} />
                </button>
              </div>
            </Row>
            <Row id="assistant.model" title="Model" description="Default model used for new chat rounds.">
              <select
                aria-label="Model"
                className="field-input h-8 w-auto px-2 text-[11px]"
                value={settings?.model ?? ''}
                disabled={!settings}
                onChange={event => changeSettings({ model: event.target.value })}
              >
                {!settings && <option value="">Loading…</option>}
                {settings && !knownModel && <option value={settings.model}>{settings.model}</option>}
                {MODEL_OPTIONS.map(option => (
                  <option key={option.value} value={option.value}>{option.label}</option>
                ))}
              </select>
            </Row>
            <Row id="assistant.thinking" title="Thinking mode" description="Let the model reason step by step before answering.">
              <Switch
                aria-label="Thinking mode"
                checked={settings?.thinkingEnabled ?? false}
                disabled={!settings}
                onCheckedChange={checked => changeSettings({ thinkingEnabled: checked })}
              />
            </Row>
            {settings?.thinkingEnabled && (
              <Row id="assistant.reasoning-effort" title="Reasoning effort" description="How hard the model thinks when thinking is enabled.">
                <select
                  aria-label="Reasoning effort"
                  className="field-input h-8 w-auto px-2 text-[11px]"
                  value={settings.reasoningEffort}
                  onChange={event => changeSettings({ reasoningEffort: event.target.value })}
                >
                  {!EFFORT_OPTIONS.includes(settings.reasoningEffort) && (
                    <option value={settings.reasoningEffort}>{settings.reasoningEffort}</option>
                  )}
                  {EFFORT_OPTIONS.map(effort => (
                    <option key={effort} value={effort}>{effort}</option>
                  ))}
                </select>
              </Row>
            )}
            <Row id="assistant.temperature" title="Temperature" description="Sampling randomness between 0 and 2.">
              {decimalControl('Temperature', settings?.temperature ?? 0, 0, 2, value => changeSettings({ temperature: value }))}
            </Row>
            <Row id="assistant.top-p" title="Top-p" description="Nucleus sampling cutoff between 0 and 1.">
              {decimalControl('Top-p', settings?.topP ?? 0, 0, 1, value => changeSettings({ topP: value }))}
            </Row>
          </Section>
        )
      }
      case 'agent-loop': {
        const fields = presentAdvancedFields(settings)
        return (
          <Section title={category.label} subtitle={category.description}>
            {fields.length === 0 ? (
              <Row id="agent-loop.empty" title="No advanced settings" description="The API host did not report any agent loop policy values.">
                {readOnlyValue('Unavailable')}
              </Row>
            ) : fields.map(field => (
              <Row key={field.key} id={`agent-loop.${field.key}`} title={field.title} description={field.description}>
                <input
                  type="number"
                  aria-label={field.title}
                  className="field-input h-8 w-24 px-2 text-[11px]"
                  min={field.min}
                  max={field.max}
                  value={settings?.[field.key] ?? ''}
                  disabled={!settings}
                  onChange={event => {
                    const current = settings?.[field.key] ?? field.min
                    const parsed = parseNumberField(event.target.value, current, field.min, field.max)
                    changeSettings({ [field.key]: parsed } as Partial<api.ChatSettings>)
                  }}
                />
              </Row>
            ))}
          </Section>
        )
      }
      case 'appearance':
        return (
          <Section title={category.label} subtitle={category.description}>
            <Row id="appearance.theme" title="Dark mode" description="Switch between the dark and light studio themes.">
              <Switch
                aria-label="Dark mode"
                checked={theme === 'dark'}
                onCheckedChange={checked => setThemePreference(checked ? 'dark' : 'light')}
              />
            </Row>
          </Section>
        )
      case 'about':
        return (
          <Section title={category.label} subtitle={category.description}>
            <Row id="about.frontend" title="Frontend origin" description="The address this studio frontend is served from.">
              <span className="font-mono text-[11px] text-muted-foreground">{window.location.origin}</span>
            </Row>
            <Row id="about.api-base" title="API base" description="Base path the frontend uses for API host requests.">
              <span className="font-mono text-[11px] text-muted-foreground">{new URL('/api', window.location.origin).toString()}</span>
            </Row>
            <Row id="about.sidecar" title="LangGraph sidecar" description="Model and mode reported by the sidecar health endpoint.">
              {readOnlyValue(
                sidecar.state === 'loading' ? 'checking…'
                  : sidecar.state === 'unreachable' ? 'unreachable via API host'
                    : `${sidecar.health.model} · ${sidecar.health.mode === 'llm' ? 'live llm' : sidecar.health.mode}`,
              )}
            </Row>
          </Section>
        )
    }
  }

  return (
    <div className="flex min-h-0 flex-1 bg-background" data-settings-page>
      <aside className="flex w-60 shrink-0 flex-col border-r" style={{ borderColor: 'var(--border)' }}>
        <div className="border-b p-3" style={{ borderColor: 'var(--border)' }}>
          <button
            className="flex items-center gap-2 text-[11px] text-muted-foreground transition-colors hover:text-foreground"
            onClick={onClose}
          >
            <ArrowLeft className="h-3.5 w-3.5" /> Back to app
          </button>
          <div className="relative mt-3">
            <Search className="pointer-events-none absolute left-2.5 top-1/2 h-3.5 w-3.5 -translate-y-1/2 text-muted-foreground" />
            <input
              aria-label="Search settings"
              className="field-input h-8 pl-8 text-[11px]"
              placeholder="Search settings"
              value={query}
              onChange={event => setQuery(event.target.value)}
            />
          </div>
        </div>
        <nav className="scrollbar-sleek min-h-0 flex-1 overflow-y-auto p-2">
          {groups.map(group => (
            <div key={group.name} className="mb-3 last:mb-0">
              <div className="px-2 py-1.5 text-[9px] font-medium uppercase tracking-[0.14em] text-muted-foreground">{group.name}</div>
              <div className="space-y-0.5">
                {group.categories.map(category => {
                  const Icon = iconMap[category.icon]
                  const isActive = !searching && category.id === active?.id
                  return (
                    <button
                      key={category.id}
                      data-settings-category={category.id}
                      className={`flex w-full items-center gap-2 rounded-md px-2 py-1.5 text-left text-[11px] transition-colors ${isActive ? 'bg-accent text-foreground' : 'text-muted-foreground hover:bg-accent/50 hover:text-foreground'}`}
                      onClick={() => setActiveCategory(category.id)}
                    >
                      <Icon className="h-3.5 w-3.5 shrink-0" /> {category.label}
                    </button>
                  )
                })}
              </div>
            </div>
          ))}
        </nav>
      </aside>
      <div className="scrollbar-sleek min-h-0 flex-1 overflow-y-auto">
        <div className="mx-auto max-w-[860px] space-y-5 p-6 lg:p-8">
          <header>
            <h1 className="text-xl font-semibold tracking-tight">{searching ? 'Settings' : (active?.label ?? 'Settings')}</h1>
            <p className="mt-1 text-[11px] text-muted-foreground">
              {searching
                ? `${categories.length} section${categories.length === 1 ? '' : 's'} match “${query.trim()}”.`
                : (active?.description ?? 'Workbench preferences.')}
            </p>
          </header>
          {visibleCategories.length === 0 ? (
            <div className="grid min-h-[200px] place-items-center rounded-xl border bg-card p-8 text-center" style={{ borderColor: 'var(--border)' }}>
              <p className="text-[11px] text-muted-foreground">No settings match{query.trim() ? ` “${query.trim()}”` : ''}.</p>
            </div>
          ) : visibleCategories.map(category => (
            <div key={category.id} data-settings-section={category.id} className="space-y-5">
              {renderCategory(category)}
            </div>
          ))}
        </div>
      </div>
    </div>
  )
}
