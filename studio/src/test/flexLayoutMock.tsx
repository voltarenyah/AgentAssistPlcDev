// Lightweight flexlayout-react stand-in for happy-dom tests. happy-dom has no
// layout engine (offsetWidth etc. are 0), so the real Layout cannot measure or
// render meaningfully. This mock preserves the semantics tests rely on: a tab
// per model tab rendered as a clickable <button> with its visible title, plus
// the factory output for the currently selected tab only (one view at a time,
// matching FlexLayout's default unmount-inactive-tabs behavior).
//
// Usage in a test file:
//   vi.mock('flexlayout-react', async () => await import('@/test/flexLayoutMock'))

/* oxlint-disable react/only-export-components -- module mock: mirrors flexlayout-react's mixed exports (Layout, Model, Actions), not a fast-refresh component file */

import { useEffect, useReducer } from 'react'

const SELECT_TAB = 'FlexLayout_SelectTab'

export const Actions = {
  SELECT_TAB,
  selectTab: (tabNodeId: string) => ({ type: SELECT_TAB, data: { tabNode: tabNodeId } }),
}

type MockAction = { type: string; data: Record<string, unknown> }

type MockTabSpec = {
  id: string
  name: string
  component?: string
}

class MockTabNode {
  private readonly spec: MockTabSpec

  constructor(spec: MockTabSpec) {
    this.spec = spec
  }

  getId() { return this.spec.id }
  getName() { return this.spec.name }
  getComponent() { return this.spec.component }
}

class MockTabSetNode {
  private readonly model: Model

  constructor(model: Model) {
    this.model = model
  }

  getType() { return 'tabset' }

  getSelectedNode() {
    const spec = this.model.tabs.find(tab => tab.id === this.model.selectedId)
    return spec ? new MockTabNode(spec) : undefined
  }
}

type ModelListener = (action: MockAction) => void

export class Model {
  tabs: MockTabSpec[] = []
  selectedId = ''
  private readonly listeners = new Set<ModelListener>()

  static fromJson(json: {
    layout?: { type?: string; children?: unknown[] }
  }): Model {
    const model = new Model()
    const walk = (node: { type?: string; id?: string; name?: string; component?: string; children?: unknown[] }) => {
      if (node.type === 'tab') {
        model.tabs.push({ id: node.id ?? '', name: node.name ?? '', component: node.component })
      }
      node.children?.forEach(child => walk(child as typeof node))
    }
    if (json.layout) walk(json.layout as Parameters<typeof walk>[0])
    model.selectedId = model.tabs[0]?.id ?? ''
    return model
  }

  doAction(action: MockAction) {
    if (action.type === SELECT_TAB) {
      this.selectedId = action.data.tabNode as string
    }
    this.listeners.forEach(listener => listener(action))
  }

  getActiveTabset() {
    return new MockTabSetNode(this)
  }

  toJson() {
    return {
      global: {},
      borders: [],
      layout: {
        type: 'row',
        children: [{
          type: 'tabset',
          id: 'tabset:workspace',
          children: this.tabs.map(tab => ({ type: 'tab', id: tab.id, name: tab.name, component: tab.component })),
        }],
      },
    }
  }

  visitNodes(fn: (node: MockTabSetNode) => void) {
    fn(new MockTabSetNode(this))
  }

  addChangeListener(listener: ModelListener) {
    this.listeners.add(listener)
  }

  removeChangeListener(listener: ModelListener) {
    this.listeners.delete(listener)
  }
}

type LayoutProps = {
  model: Model
  factory: (node: MockTabNode) => React.ReactNode
}

export function Layout({ model, factory }: LayoutProps) {
  const [, bump] = useReducer((tick: number) => tick + 1, 0)

  useEffect(() => {
    const listener = () => bump()
    model.addChangeListener(listener)
    return () => model.removeChangeListener(listener)
  }, [model])

  const selected = model.tabs.find(tab => tab.id === model.selectedId)

  return (
    <div data-flexlayout-mock="">
      <div role="tablist">
        {model.tabs.map(tab => (
          <button
            key={tab.id}
            role="tab"
            aria-selected={tab.id === model.selectedId}
            onClick={() => model.doAction(Actions.selectTab(tab.id))}
          >
            {tab.name}
          </button>
        ))}
      </div>
      <div data-flexlayout-mock-content="">
        {selected ? factory(new MockTabNode(selected)) : null}
      </div>
    </div>
  )
}
