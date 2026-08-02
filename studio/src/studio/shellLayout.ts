export type DockSide = 'left' | 'right'

export type ShellLayout = {
  version: 1
  leftOpen: boolean
  rightOpen: boolean
  leftWidth: number
  rightWidth: number
}

export const SHELL_LAYOUT_STORAGE_KEY = 'plc-studio.shell-layout.v1'

export const DEFAULT_SHELL_LAYOUT: ShellLayout = {
  version: 1,
  leftOpen: true,
  rightOpen: true,
  leftWidth: 310,
  rightWidth: 320,
}

export const clampDockWidth = (_side: DockSide, value: number) =>
  Math.round(Math.max(240, Math.min(420, value)))

export const readShellLayout = (storage: Storage | null): ShellLayout => {
  if (!storage) return DEFAULT_SHELL_LAYOUT
  try {
    const raw = storage.getItem(SHELL_LAYOUT_STORAGE_KEY)
    if (!raw) return DEFAULT_SHELL_LAYOUT
    const parsed = JSON.parse(raw) as Partial<ShellLayout>
    if (parsed.version !== 1 || typeof parsed.leftOpen !== 'boolean' || typeof parsed.rightOpen !== 'boolean') {
      return DEFAULT_SHELL_LAYOUT
    }
    if (typeof parsed.leftWidth !== 'number' || typeof parsed.rightWidth !== 'number'
      || !Number.isFinite(parsed.leftWidth) || !Number.isFinite(parsed.rightWidth)) return DEFAULT_SHELL_LAYOUT
    return {
      version: 1,
      leftOpen: parsed.leftOpen,
      rightOpen: parsed.rightOpen,
      leftWidth: clampDockWidth('left', parsed.leftWidth),
      rightWidth: clampDockWidth('right', parsed.rightWidth),
    }
  } catch {
    return DEFAULT_SHELL_LAYOUT
  }
}

export const writeShellLayout = (storage: Storage | null, layout: ShellLayout) => {
  storage?.setItem(SHELL_LAYOUT_STORAGE_KEY, JSON.stringify({
    version: 1,
    leftOpen: layout.leftOpen,
    rightOpen: layout.rightOpen,
    leftWidth: clampDockWidth('left', layout.leftWidth),
    rightWidth: clampDockWidth('right', layout.rightWidth),
  }))
}
