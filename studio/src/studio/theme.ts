import { useSyncExternalStore } from 'react'

export type ThemeMode = 'dark' | 'light'
/** Alias kept for callers written against the original naming. */
export type ThemePreference = ThemeMode

export const THEME_STORAGE_KEY = 'plc-studio.theme.v1'

export const DEFAULT_THEME: ThemeMode = 'dark'

type ThemeListener = (theme: ThemeMode) => void

const listeners = new Set<ThemeListener>()
let current: ThemeMode | null = null

export const readTheme = (storage: Storage | null): ThemeMode => {
  if (!storage) return DEFAULT_THEME
  try {
    const raw = storage.getItem(THEME_STORAGE_KEY)
    return raw === 'dark' || raw === 'light' ? raw : DEFAULT_THEME
  } catch {
    return DEFAULT_THEME
  }
}

/** Alias kept for callers written against the original naming. */
export const readStoredTheme = readTheme

const safeStorage = (): Storage | null => {
  try { return typeof window === 'undefined' ? null : window.localStorage } catch { return null }
}

export const applyTheme = (mode: ThemeMode, root?: HTMLElement) => {
  const target = root ?? (typeof document === 'undefined' ? null : document.documentElement)
  if (!target) return
  target.classList.toggle('dark', mode === 'dark')
}

export const writeTheme = (storage: Storage | null, mode: ThemeMode) => {
  try { storage?.setItem(THEME_STORAGE_KEY, mode) } catch { /* storage is optional */ }
}

export const getThemePreference = (): ThemeMode => {
  if (current === null) {
    current = readTheme(safeStorage())
    applyTheme(current)
  }
  return current
}

export const setThemePreference = (mode: ThemeMode, storage?: Storage | null) => {
  current = mode
  applyTheme(mode)
  writeTheme(storage === undefined ? safeStorage() : storage, mode)
  listeners.forEach(listener => listener(mode))
}

export const subscribeTheme = (listener: ThemeListener): (() => void) => {
  listeners.add(listener)
  return () => { listeners.delete(listener) }
}

export const useThemePreference = (): ThemeMode =>
  useSyncExternalStore(subscribeTheme, getThemePreference, () => DEFAULT_THEME)

/** Test helper: drop the cached preference so the next read hits storage again. */
export const resetThemeCacheForTests = () => { current = null }
