export type DockSide = 'left' | 'right'

/** The right dock's pages, in the order the rail presents them. */
export const RIGHT_DOCK_PAGES = ['properties', 'changes', 'history'] as const
export type RightDockPage = (typeof RIGHT_DOCK_PAGES)[number]

export type ShellLayout = {
  version: 2
  leftOpen: boolean
  leftWidth: number
  /** Whether the whole right column, rail included, is shown. The title bar's toggle owns it. */
  rightColumnOpen: boolean
  /** The open page's width. The rail and the resize handle are added to it (ADR-0015). */
  rightPanelWidth: number
  /**
   * The page the rail marks, or `null` before the user has ever chosen one — `null` lets the first
   * session follow the selection instead of forcing a page nobody picked.
   */
  rightPanel: RightDockPage | null
  /** Whether that page is collapsed to the rail. Kept apart from the page so the rail can mark it. */
  rightPanelCollapsed: boolean
}

export const SHELL_LAYOUT_STORAGE_KEY = 'plc-studio.shell-layout.v2'
const LEGACY_SHELL_LAYOUT_STORAGE_KEY = 'plc-studio.shell-layout.v1'

/** The rail is a fixed strip inside the right column, so only the page is resized. */
export const RIGHT_DOCK_RAIL_WIDTH = 44

const MIN_DOCK_WIDTH = 240
const MAX_DOCK_WIDTH = 420
/**
 * The right column's page holds the version-control toolbar (Compare, the scope toggle, verify), which
 * needs about 264 px before its own padding; below that the row wraps and the page reads as broken.
 * The floor is therefore the default page width itself, which also keeps a migrated v1 layout exactly
 * as wide as the column it replaced (310 px total). The rail adds its fixed 44 px on top.
 */
const MIN_RIGHT_PANEL_WIDTH = 266

export const DEFAULT_SHELL_LAYOUT: ShellLayout = {
  version: 2,
  leftOpen: true,
  leftWidth: 310,
  rightColumnOpen: true,
  // The default column keeps the 310 px it held before the rail existed: 44 px rail + 266 px page.
  rightPanelWidth: 310 - RIGHT_DOCK_RAIL_WIDTH,
  rightPanel: null,
  rightPanelCollapsed: false,
}

export const clampDockWidth = (side: DockSide, value: number) => {
  const minimum = side === 'right' ? MIN_RIGHT_PANEL_WIDTH : MIN_DOCK_WIDTH
  return Math.round(Math.max(minimum, Math.min(MAX_DOCK_WIDTH, value)))
}

const isRightPage = (value: unknown): value is RightDockPage =>
  (RIGHT_DOCK_PAGES as readonly string[]).includes(value as string)

const finiteNumber = (value: unknown): value is number =>
  typeof value === 'number' && Number.isFinite(value)

/** Reads the current shape, or null when the payload is not one. Raises on unparseable input. */
const readV2 = (raw: string): ShellLayout | null => {
  const parsed = JSON.parse(raw) as Partial<ShellLayout>
  if (parsed.version !== 2
    || typeof parsed.leftOpen !== 'boolean'
    || typeof parsed.rightColumnOpen !== 'boolean'
    || typeof parsed.rightPanelCollapsed !== 'boolean'
    || !finiteNumber(parsed.leftWidth)
    || !finiteNumber(parsed.rightPanelWidth)
    || (parsed.rightPanel != null && !isRightPage(parsed.rightPanel))) {
    return null
  }
  return {
    version: 2,
    leftOpen: parsed.leftOpen,
    leftWidth: clampDockWidth('left', parsed.leftWidth),
    rightColumnOpen: parsed.rightColumnOpen,
    rightPanelWidth: clampDockWidth('right', parsed.rightPanelWidth),
    rightPanel: parsed.rightPanel ?? null,
    rightPanelCollapsed: parsed.rightPanelCollapsed,
  }
}

/**
 * A v1 layout stores the right dock as one column with no rail, so its width is the page's width
 * plus the rail. Migrating subtracts the rail, which keeps the column's total width — and so the
 * workspace — exactly where the user left it.
 */
const readV1 = (raw: string): ShellLayout | null => {
  const parsed = JSON.parse(raw) as {
    version?: number
    leftOpen?: boolean
    leftWidth?: number
    rightOpen?: boolean
    rightWidth?: number
  }
  if (parsed.version !== 1
    || typeof parsed.leftOpen !== 'boolean'
    || typeof parsed.rightOpen !== 'boolean'
    || !finiteNumber(parsed.leftWidth)
    || !finiteNumber(parsed.rightWidth)) {
    return null
  }
  return {
    version: 2,
    leftOpen: parsed.leftOpen,
    leftWidth: clampDockWidth('left', parsed.leftWidth),
    rightColumnOpen: parsed.rightOpen,
    rightPanelWidth: clampDockWidth('right', parsed.rightWidth - RIGHT_DOCK_RAIL_WIDTH),
    rightPanel: null,
    rightPanelCollapsed: false,
  }
}

export const readShellLayout = (storage: Storage | null): ShellLayout => {
  if (!storage) return DEFAULT_SHELL_LAYOUT
  try {
    const raw = storage.getItem(SHELL_LAYOUT_STORAGE_KEY)
    if (raw) return readV2(raw) ?? DEFAULT_SHELL_LAYOUT
    const legacy = storage.getItem(LEGACY_SHELL_LAYOUT_STORAGE_KEY)
    return legacy ? readV1(legacy) ?? DEFAULT_SHELL_LAYOUT : DEFAULT_SHELL_LAYOUT
  } catch {
    return DEFAULT_SHELL_LAYOUT
  }
}

export const writeShellLayout = (storage: Storage | null, layout: ShellLayout) => {
  storage?.setItem(SHELL_LAYOUT_STORAGE_KEY, JSON.stringify({
    version: 2,
    leftOpen: layout.leftOpen,
    leftWidth: clampDockWidth('left', layout.leftWidth),
    rightColumnOpen: layout.rightColumnOpen,
    rightPanelWidth: clampDockWidth('right', layout.rightPanelWidth),
    rightPanel: layout.rightPanel ?? null,
    rightPanelCollapsed: layout.rightPanelCollapsed,
  }))
}
