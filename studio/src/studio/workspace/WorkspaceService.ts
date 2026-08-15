// WorkspaceService is the semantic controller for the device workspace. It
// owns the FlexLayout model (geometry) and exposes intent-level navigation
// operations so callers (MainStudio today, agent/MCP workflows later) never
// touch FlexLayout Actions or the model directly. Domain payloads (chat
// context, device ids, …) never enter the model — callers set their own state
// and then ask the service to focus a view.
//
// One instance per application/workspace lifecycle: MainStudio creates it via
// useState(() => new WorkspaceService(...)). No module-global singleton.
// The service itself is storage-agnostic: persistence is injected via the
// optional onLayoutChange callback (invoked with model.toJson() after every
// model action; at this scale serializing per change is cheap).

import { Actions, Model, type Action, type IJsonModel, type TabSetNode } from 'flexlayout-react'
import { buildDefaultWorkspaceLayout } from './defaultLayout'
import {
  workspaceViewInstanceId,
  workspaceViewKindForInstanceId,
  type WorkspaceViewKind,
} from './workspaceTypes'

export type WorkspaceFocusListener = (kind: WorkspaceViewKind | null) => void

/** Stable-identity snapshot for useSyncExternalStore; replaced on reset. */
export type WorkspaceModelSnapshot = { model: Model; version: number }

export class WorkspaceService {
  private snapshot: WorkspaceModelSnapshot
  private focusedKind: WorkspaceViewKind | null
  private readonly listeners = new Set<WorkspaceFocusListener>()
  private readonly modelListeners = new Set<() => void>()
  private readonly onLayoutChange?: (layout: IJsonModel) => void

  /** `savedLayout` is the persisted layout from workspaceLayoutStorage (null → default). */
  constructor(savedLayout?: IJsonModel | null, onLayoutChange?: (layout: IJsonModel) => void) {
    this.onLayoutChange = onLayoutChange
    this.snapshot = { model: Model.fromJson(savedLayout ?? buildDefaultWorkspaceLayout()), version: 0 }
    this.focusedKind = this.initialFocusedKind()
    this.model.addChangeListener(this.handleModelAction)
  }

  private get model(): Model {
    return this.snapshot.model
  }

  /** Geometry pass-through for WorkspaceHost's <Layout>. */
  getModel(): Model {
    return this.model
  }

  getModelSnapshot(): WorkspaceModelSnapshot {
    return this.snapshot
  }

  /** Fires when the model instance is replaced (resetLayout). */
  subscribeModel(listener: () => void): () => void {
    this.modelListeners.add(listener)
    return () => {
      this.modelListeners.delete(listener)
    }
  }

  getFocusedViewKind(): WorkspaceViewKind | null {
    return this.focusedKind
  }

  /** Notified when the focused view kind changes; returns an unsubscribe. */
  subscribe(listener: WorkspaceFocusListener): () => void {
    this.listeners.add(listener)
    return () => {
      this.listeners.delete(listener)
    }
  }

  /**
   * V1: every view's tab always exists, so open == focus. Kept as a separate
   * method so multi-instance semantics (open creates a new tab) can differ
   * later without changing call sites.
   */
  openView(kind: WorkspaceViewKind): void {
    this.focusView(kind)
  }

  focusView(kind: WorkspaceViewKind): void {
    this.model.doAction(Actions.selectTab(workspaceViewInstanceId(kind)))
  }

  /** Semantic alias: intent is "inspect the PLC source", not geometry. */
  showSource(): void {
    this.focusView('source')
  }

  /** Semantic alias: intent is "review/merge changes", not geometry. */
  showDiff(): void {
    this.focusView('git')
  }

  /**
   * Replaces the model with the default layout (single tabset, five views).
   * Notifies model subscribers so hosts remount the Layout, restores focus to
   * the overview, and reports the default geometry via onLayoutChange (which
   * overwrites any persisted custom layout).
   */
  resetLayout(): void {
    this.snapshot = { model: Model.fromJson(buildDefaultWorkspaceLayout()), version: this.snapshot.version + 1 }
    this.model.addChangeListener(this.handleModelAction)
    this.modelListeners.forEach(listener => listener())
    this.setFocused(this.initialFocusedKind())
    this.onLayoutChange?.(this.model.toJson())
  }

  private readonly handleModelAction = (action: Action): void => {
    // Covers Layout-internal actions as well as our own doAction calls.
    // Selecting a tab also activates its tabset, so tracking the selected
    // tab directly is equivalent to "selected tab of the active tabset"
    // (getActiveTabset() is unreliable before the first interaction).
    if (action.type === Actions.SELECT_TAB) {
      this.setFocused(workspaceViewKindForInstanceId(action.data.tabNode as string))
    } else if (action.type === Actions.MOVE_NODE
      || action.type === Actions.ADD_TAB
      || action.type === Actions.DELETE_TAB
      || action.type === Actions.DELETE_TABSET
      || action.type === Actions.SET_ACTIVE_TABSET) {
      // Geometry actions can move focus without a SELECT_TAB (e.g. moveNode
      // with select=true); the active tabset is reliable once one exists.
      this.setFocused(this.activeTabsetFocusedKind())
    }
    this.onLayoutChange?.(this.model.toJson())
  }

  private initialFocusedKind(): WorkspaceViewKind | null {
    // A restored layout may already carry an active tabset; a fresh default
    // layout has none, so fall back to the selected tab of the first tabset.
    const active = this.activeTabsetFocusedKind()
    if (active !== null) return active
    let kind: WorkspaceViewKind | null = null
    this.model.visitNodes(node => {
      if (kind !== null || node.getType() !== 'tabset') return
      const selectedId = (node as TabSetNode).getSelectedNode()?.getId()
      kind = selectedId ? workspaceViewKindForInstanceId(selectedId) : null
    })
    return kind
  }

  private activeTabsetFocusedKind(): WorkspaceViewKind | null {
    const id = this.model.getActiveTabset()?.getSelectedNode()?.getId()
    return id ? workspaceViewKindForInstanceId(id) : null
  }

  private setFocused(next: WorkspaceViewKind | null): void {
    // Focusing the already-focused view fires no notification.
    if (next === this.focusedKind) return
    this.focusedKind = next
    this.listeners.forEach(listener => listener(next))
  }
}
