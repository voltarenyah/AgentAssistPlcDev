# Dockable Workbench Shell Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add independent, resizable, persistent left and right docks to the Studio shell, with title-bar toggles, a full-width status bar, and a working settings control.

**Architecture:** Keep `MainStudio` as the shell owner and keep the existing navigator/context dock components content-focused. Add a small pure `shellLayout` module for defaults, clamping, and local-storage parsing so persistence and width behavior can be tested without rendering the whole Studio.

**Tech Stack:** React 19, TypeScript, Tailwind CSS 4, lucide-react, Vitest, happy-dom.

---

### Task 1: Add a tested shell-layout state module

**Files:**
- Create: `studio/src/studio/shellLayout.ts`
- Test: `studio/src/studio/shellLayout.test.ts`

- [ ] **Step 1: Write failing tests for defaults, clamping, persistence, and malformed storage**

Test the public behavior with a `Storage` mock:

```ts
import { describe, expect, it } from 'vitest'
import {
  DEFAULT_SHELL_LAYOUT,
  clampDockWidth,
  readShellLayout,
  writeShellLayout,
} from './shellLayout'

describe('shell layout', () => {
  it('uses open docks and reference widths by default', () => {
    expect(readShellLayout(null)).toEqual(DEFAULT_SHELL_LAYOUT)
  })

  it('clamps left and right widths to their safe ranges', () => {
    expect(clampDockWidth('left', 100)).toBe(240)
    expect(clampDockWidth('left', 999)).toBe(420)
    expect(clampDockWidth('right', 100)).toBe(240)
    expect(clampDockWidth('right', 999)).toBe(420)
  })

  it('round-trips a valid layout', () => {
    const storage = new Map<string, string>()
    const adapter: Storage = {
      getItem: key => storage.get(key) ?? null,
      setItem: (key, value) => { storage.set(key, value) },
      removeItem: key => { storage.delete(key) },
      clear: () => { storage.clear() },
      key: index => [...storage.keys()][index] ?? null,
      get length() { return storage.size },
    }
    const value = { leftOpen: false, rightOpen: true, leftWidth: 280, rightWidth: 360 }
    writeShellLayout(adapter, value)
    expect(readShellLayout(adapter)).toEqual(value)
  })

  it('falls back safely for malformed or out-of-range values', () => {
    const storage = { getItem: () => '{"version":99,"leftOpen":"yes"}' } as Storage
    expect(readShellLayout(storage)).toEqual(DEFAULT_SHELL_LAYOUT)
  })
})
```

- [ ] **Step 2: Run the focused test and confirm it fails because the module is missing**

Run from `studio`:

```text
npm test -- shellLayout.test.ts
```

Expected: FAIL with the module/import not found.

- [ ] **Step 3: Implement the pure module**

Export the versioned storage key, layout type, defaults, side-specific width limits, and these functions:

```ts
export type DockSide = 'left' | 'right'
export type ShellLayout = {
  version: 1
  leftOpen: boolean
  rightOpen: boolean
  leftWidth: number
  rightWidth: number
}

export const DEFAULT_SHELL_LAYOUT: ShellLayout = {
  version: 1,
  leftOpen: true,
  rightOpen: true,
  leftWidth: 310,
  rightWidth: 320,
}

export const clampDockWidth = (side: DockSide, value: number) =>
  Math.round(Math.max(240, Math.min(side === 'left' ? 420 : 420, value)))

export const readShellLayout = (storage: Storage | null): ShellLayout => {
  if (!storage) return DEFAULT_SHELL_LAYOUT
  try {
    const raw = storage.getItem(SHELL_LAYOUT_STORAGE_KEY)
    if (!raw) return DEFAULT_SHELL_LAYOUT
    const parsed = JSON.parse(raw) as Partial<ShellLayout>
    if (parsed.version !== 1 || typeof parsed.leftOpen !== 'boolean' || typeof parsed.rightOpen !== 'boolean') {
      return DEFAULT_SHELL_LAYOUT
    }
    if (!Number.isFinite(parsed.leftWidth) || !Number.isFinite(parsed.rightWidth)) return DEFAULT_SHELL_LAYOUT
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
```

Use `globalThis.localStorage` only at the `MainStudio` call site; keep this module safe in tests and non-browser contexts.

- [ ] **Step 4: Run the focused test and confirm it passes**

Run:

```text
npm test -- shellLayout.test.ts
```

Expected: all shell-layout tests pass.

### Task 2: Add independent dock state and title-bar controls

**Files:**
- Modify: `studio/src/studio/MainStudio.tsx`
- Modify: `studio/src/studio/MainStudio.apiKey.test.tsx`
- Modify: `studio/src/studio/MainStudio.deviceSelect.test.tsx`

- [ ] **Step 1: Add shell state and persistence wiring**

Import `PanelLeftClose`, `PanelLeftOpen`, `PanelRightClose`, `PanelRightOpen`, and `Settings2` from `lucide-react`, plus `clampDockWidth`, `readShellLayout`, `writeShellLayout`, and `type DockSide`, `type ShellLayout` from `./shellLayout`.

Replace `sessionDockVisible` with:

```ts
const [shellLayout, setShellLayout] = useState<ShellLayout>(() => {
  try {
    return readShellLayout(window.localStorage)
  } catch {
    return readShellLayout(null)
  }
})

useEffect(() => {
  try { writeShellLayout(window.localStorage, shellLayout) } catch { /* storage is optional */ }
}, [shellLayout])
```

Add a `toggleDock(side: DockSide)` callback that flips only `leftOpen` or `rightOpen`, and a resize callback that clamps only the selected side's width.

- [ ] **Step 2: Add regression tests for independent collapse/reopen and persistent state**

Use the existing `MainStudio` happy-dom render helper. Add `localStorage.clear()` in `beforeEach`, then assert:

```ts
const leftToggle = host.querySelector<HTMLButtonElement>('[data-dock-toggle="left"]')
const rightToggle = host.querySelector<HTMLButtonElement>('[data-dock-toggle="right"]')
expect(host.querySelector('[data-dock="left"]')).not.toBeNull()
expect(host.querySelector('[data-dock="right"]')).not.toBeNull()

act(() => leftToggle?.click())
expect(host.querySelector('[data-dock="left"]')).toBeNull()
expect(host.querySelector('[data-dock="right"]')).not.toBeNull()

act(() => rightToggle?.click())
expect(host.querySelector('[data-dock="right"]')).toBeNull()
expect(host.querySelector('[data-status-bar]')).not.toBeNull()

act(() => leftToggle?.click())
expect(host.querySelector('[data-dock="left"]')).not.toBeNull()
```

Also verify that opening the right dock on Overview, Knowledge, or chat does not change the selected tab/content. Keep the current API-key and device-selection assertions intact.

- [ ] **Step 3: Run the focused tests and confirm they fail for the new selectors**

Run:

```text
npm test -- MainStudio.apiKey.test.tsx MainStudio.deviceSelect.test.tsx
```

Expected: the new dock assertions fail because the shell controls/selectors do not exist yet.

- [ ] **Step 4: Render the title-bar toggles**

Place the left toggle at the start of the title bar and the right toggle with the other shell controls. The controls must be present even with no selected device:

```tsx
<button
  data-dock-toggle="left"
  className="icon-button"
  aria-label={shellLayout.leftOpen ? 'Hide workbench project tree' : 'Show workbench project tree'}
  title={shellLayout.leftOpen ? 'Hide workbench project tree' : 'Show workbench project tree'}
  onClick={() => toggleDock('left')}
>
  {shellLayout.leftOpen ? <PanelLeftClose /> : <PanelLeftOpen />}
</button>
```

Mirror the same pattern for `data-dock-toggle="right"` and the context dock. Remove the conditional `selection.deviceId &&` around the existing right toggle.

- [ ] **Step 5: Run the focused tests and confirm they pass**

Run:

```text
npm test -- MainStudio.apiKey.test.tsx MainStudio.deviceSelect.test.tsx
```

Expected: existing API-key/device-selection tests and new dock-state assertions pass.

### Task 3: Make the body a true zero-width dock shell

**Files:**
- Modify: `studio/src/studio/MainStudio.tsx`
- Modify: `studio/src/studio/workbench/WorkbenchNavigator.tsx`
- Modify: `studio/src/studio/chat/SessionDock.tsx`
- Modify: `studio/src/studio/DevicePropertiesDock.tsx`
- Modify: `studio/src/studio/KnowledgePropertiesDock.tsx`

- [ ] **Step 1: Add shell data attributes and width styles**

Render the left navigator only when `shellLayout.leftOpen` and pass `style={{ width: shellLayout.leftWidth }}`. Change the navigator root from a hard-coded `w-[310px]` to `w-auto` while retaining `shrink-0` and its border.

Render the active right dock only when `shellLayout.rightOpen`, wrap it with `data-dock="right"`, and pass `style={{ width: shellLayout.rightWidth }}`. Change each right dock root from hard-coded `w-[320px]` to `w-auto`.

Add `data-dock="left"` to the navigator root and `data-status-bar` to the new footer.

- [ ] **Step 2: Add resize handles with pointer capture**

Place a `role="separator"` between each open dock and the main content. On pointer down, record the side, starting pointer X, and starting width; on pointer move, set `leftWidth` to `startWidth + deltaX` for the left dock and `rightWidth` to `startWidth - deltaX` for the right dock; on pointer up, remove listeners. Always call `clampDockWidth` before storing.

Use `aria-orientation="vertical"`, `aria-label="Resize workbench project tree"` / `"Resize context dock"`, and `cursor-col-resize`. Hide the handle completely when the dock is closed.

- [ ] **Step 3: Keep right-dock content behavior unchanged**

Replace each `hidden={sessionDockVisible}` prop with `hidden={false}` or remove the prop from the content components after the shell controls rendering. Do not change the tab-to-dock selection rules or domain data flow.

- [ ] **Step 4: Run the relevant UI suite**

Run:

```text
npm test -- MainStudio.apiKey.test.tsx MainStudio.deviceSelect.test.tsx MainStudio.contract.test.ts
```

Expected: all existing and new shell behavior tests pass.

### Task 4: Add the compact status bar and settings entry point

**Files:**
- Modify: `studio/src/studio/MainStudio.tsx`
- Modify: `studio/src/assets/main.css`

- [ ] **Step 1: Add the footer markup**

Below the body shell, add a full-width footer:

```tsx
<footer data-status-bar className="studio-status-bar">
  <span className="studio-status-indicator">● Ready</span>
  <span>{deviceInfo?.plcName ?? 'No device'}</span>
  <span className="font-mono">{activeWorktree?.branch ?? 'no worktree'}</span>
  <span className="flex-1" />
  <span>{sessions.length} TIA session{sessions.length === 1 ? '' : 's'}</span>
  <button className="icon-button" aria-label="Refresh status" title="Refresh status" onClick={() => void loadStartup()}>
    <RefreshCw className="h-3 w-3" />
  </button>
  <button className="icon-button" aria-label="Settings" title="Settings" onClick={() => setApiKeyDialogOpen(true)}>
    <Settings2 className="h-3.5 w-3.5" />
  </button>
</footer>
```

Keep the existing API-status button in the title bar; the footer settings button reuses the current API-key dialog and is not decorative.

- [ ] **Step 2: Add restrained status-bar styling**

Add `.studio-status-bar` and `.studio-status-indicator` to `studio/src/assets/main.css` using existing variables: `height: 28px`, `border-top: 1px solid var(--border)`, `background: var(--card)`, compact `font-size`, and `color: var(--muted-foreground)`. Add an emerald status color and ensure the footer remains visible in dark mode.

- [ ] **Step 3: Test the footer/settings behavior**

Extend the API-key test to click `[aria-label="Settings"]`, assert the password input appears, close it, and assert the footer remains after both dock toggles. Run:

```text
npm test -- MainStudio.apiKey.test.tsx
```

Expected: settings opens the existing dialog and the footer is present in both-dock-collapsed state.

### Task 5: Build, full test, and manual visual verification

**Files:**
- Verify: `studio/src/studio/shellLayout.ts`
- Verify: `studio/src/studio/MainStudio.tsx`
- Verify: `studio/src/assets/main.css`

- [ ] **Step 1: Run the full Studio test suite**

Run from `studio`:

```text
npm test
```

Expected: all existing and new Vitest tests pass.

- [ ] **Step 2: Run the production build**

Run:

```text
npm run build
```

Expected: TypeScript and Vite complete successfully with no new errors.

- [ ] **Step 3: Manually verify the three shell states**

With the Studio running, verify:

1. both docks open at startup;
2. left collapsed/right open — center expands leftward;
3. left open/right collapsed — center expands rightward;
4. both collapsed — center spans the body with no edge rail;
5. reopen restores each prior width;
6. changing Overview/Knowledge/chat while the right dock is closed does not reopen it;
7. status bar and settings remain visible in all states;
8. dark theme retains readable borders, controls, and status text.

- [ ] **Step 4: Review the final diff and leave unrelated changes untouched**

Run:

```text
git diff --check
git status --short
```

Expected: only the intended Studio shell files are changed by this task; pre-existing user changes remain present and are not staged or committed.
