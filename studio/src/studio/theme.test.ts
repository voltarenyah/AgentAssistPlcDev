// @vitest-environment happy-dom
import { afterEach, describe, expect, it, vi } from 'vitest'
import {
  DEFAULT_THEME,
  THEME_STORAGE_KEY,
  applyTheme,
  getThemePreference,
  readStoredTheme,
  resetThemeCacheForTests,
  setThemePreference,
  subscribeTheme,
} from './theme'

afterEach(() => {
  resetThemeCacheForTests()
  window.localStorage.clear()
  document.documentElement.classList.remove('dark')
})

describe('theme', () => {
  it('defaults to dark when nothing is stored', () => {
    expect(readStoredTheme(window.localStorage)).toBe(DEFAULT_THEME)
    expect(getThemePreference()).toBe('dark')
  })

  it('reads a stored preference and ignores invalid values', () => {
    window.localStorage.setItem(THEME_STORAGE_KEY, 'light')
    expect(readStoredTheme(window.localStorage)).toBe('light')
    window.localStorage.setItem(THEME_STORAGE_KEY, 'neon')
    expect(readStoredTheme(null)).toBe('dark')
    expect(readStoredTheme(window.localStorage)).toBe('dark')
  })

  it('persists, applies, and notifies on change', () => {
    const listener = vi.fn()
    const unsubscribe = subscribeTheme(listener)

    setThemePreference('light')

    expect(window.localStorage.getItem(THEME_STORAGE_KEY)).toBe('light')
    expect(document.documentElement.classList.contains('dark')).toBe(false)
    expect(getThemePreference()).toBe('light')
    expect(listener).toHaveBeenCalledTimes(1)

    setThemePreference('dark')
    expect(document.documentElement.classList.contains('dark')).toBe(true)
    expect(listener).toHaveBeenCalledTimes(2)

    unsubscribe()
    setThemePreference('light')
    expect(listener).toHaveBeenCalledTimes(2)
  })

  it('applies the theme to an explicit root element', () => {
    const root = document.createElement('html')
    applyTheme('dark', root)
    expect(root.classList.contains('dark')).toBe(true)
    applyTheme('light', root)
    expect(root.classList.contains('dark')).toBe(false)
  })
})
