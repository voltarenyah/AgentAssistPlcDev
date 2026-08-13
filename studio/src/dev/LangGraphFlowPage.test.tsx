// @vitest-environment happy-dom
import { act } from 'react'
import { createRoot } from 'react-dom/client'
import { afterEach, describe, expect, it } from 'vitest'
import LangGraphFlowPage, { isLangGraphFlowDevRoute } from './LangGraphFlowPage'

globalThis.IS_REACT_ACT_ENVIRONMENT = true

const render = async () => {
  const host = document.createElement('div')
  document.body.appendChild(host)
  const root = createRoot(host)
  await act(async () => root.render(<LangGraphFlowPage />))
  return { host, root }
}

afterEach(() => { document.body.innerHTML = '' })

describe('LangGraphFlowPage', () => {
  it('only enables the direct page in development with the exact query', () => {
    expect(isLangGraphFlowDevRoute('http://localhost/?dev=langgraph-flow', true)).toBe(true)
    expect(isLangGraphFlowDevRoute('http://localhost/?dev=langgraph-flow', false)).toBe(false)
    expect(isLangGraphFlowDevRoute('http://localhost/', true)).toBe(false)
  })

  it('renders the static explainer without making API calls', async () => {
    const fetchSpy = globalThis.fetch
    globalThis.fetch = (() => { throw new Error('The explainer must not fetch') }) as typeof fetch
    const { host } = await render()
    expect(host.textContent).toContain('How the LangGraph workflow moves.')
    expect(host.textContent).toContain('bootstrap_context')
    globalThis.fetch = fetchSpy
  })

  it('highlights the mutation path and pins node detail', async () => {
    const { host } = await render()
    const mutationButton = Array.from(host.querySelectorAll('button')).find(button => button.textContent === 'Mutation + approval')!
    await act(async () => mutationButton.click())
    expect(host.querySelector('[data-node-id="lg-interrupt"]')?.getAttribute('data-active')).toBe('true')
    const proposeButton = host.querySelector<HTMLButtonElement>('[data-node-id="lg-propose"]')!
    await act(async () => proposeButton.click())
    expect(host.querySelector('.flow-inspector')?.textContent).toContain('No mutation call before approval.')
    expect(host.querySelector('.flow-inspector')?.textContent).toContain('agent-service/app_assistant/graph.py')
  })

  it('makes hover and keyboard focus reveal the same concise detail', async () => {
    const { host } = await render()
    const decideButton = host.querySelector<HTMLButtonElement>('[data-node-id="lg-decide"]')!
    await act(async () => decideButton.dispatchEvent(new MouseEvent('mouseover', { bubbles: true })))
    expect(host.querySelector('.flow-hover-note')?.textContent).toContain('Classify the explicit command')
    await act(async () => decideButton.focus())
    expect(host.querySelector('.flow-hover-note')?.textContent).toContain('Classify the explicit command')
  })
})
