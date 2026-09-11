// @vitest-environment happy-dom
import React, { act } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { afterEach, describe, expect, it, vi } from 'vitest'
import type { OperationStatus } from '@/api/client'
import OperationTimingList from './OperationTimingList'

globalThis.IS_REACT_ACT_ENVIRONMENT = true

const status: OperationStatus = {
  operationId: 'compare-1',
  operationType: 'compare-tia',
  state: 'running',
  message: 'Comparing project source…',
  updatedAt: '2026-09-11T00:00:00Z',
  errorMessage: null,
  completedPhases: [],
  currentPhase: {
    message: 'Comparing project source…',
    startedAt: '2026-09-11T00:00:00Z',
    completedAt: null,
    elapsedMilliseconds: 1000,
  },
}

const roots: Root[] = []
const render = (value: OperationStatus, layout: 'inline' | 'dashboard' = 'inline') => {
  const host = document.createElement('div')
  document.body.appendChild(host)
  const root = createRoot(host)
  roots.push(root)
  act(() => root.render(<OperationTimingList status={value} layout={layout} />))
  return { host, root }
}

afterEach(() => {
  act(() => roots.splice(0).forEach(root => root.unmount()))
  vi.useRealTimers()
  document.body.innerHTML = ''
})

describe('OperationTimingList', () => {
  it.each(['inline', 'dashboard'] as const)('advances an active phase between status polls in %s layout', (layout) => {
    vi.useFakeTimers()
    vi.setSystemTime(new Date('2026-09-11T00:00:00Z'))
    const { host } = render(status, layout)

    expect(host.textContent).toContain('1.0 s')

    act(() => vi.advanceTimersByTime(2000))

    expect(host.textContent).toContain('3.0 s')
  })

  it('advances source-export activity and resets when the phase changes', () => {
    vi.useFakeTimers()
    const { host, root } = render({ ...status, currentPhase: { ...status.currentPhase!, message: 'Exporting block Main...' } }, 'dashboard')
    act(() => vi.advanceTimersByTime(2000))
    expect(host.querySelector('[data-source-export-activity] time')?.textContent).toBe('3.0 s')

    act(() => root.render(<OperationTimingList layout="dashboard" status={{ ...status, currentPhase: { ...status.currentPhase!, message: 'Reading checksum', elapsedMilliseconds: 500 } }} />))
    expect(host.querySelector('time')?.textContent).toBe('500 ms')
    act(() => vi.advanceTimersByTime(1000))
    expect(host.querySelector('time')?.textContent).toBe('1.5 s')
  })

  it('resynchronizes with a fresh server measurement and stops on completion', () => {
    vi.useFakeTimers()
    const { host, root } = render(status)
    act(() => vi.advanceTimersByTime(2000))
    const fresh = { ...status, currentPhase: { ...status.currentPhase!, elapsedMilliseconds: 4000 } }
    act(() => root.render(<OperationTimingList status={fresh} />))
    expect(host.querySelector('time')?.textContent).toBe('4.0 s')
    act(() => vi.advanceTimersByTime(1000))
    expect(host.querySelector('time')?.textContent).toBe('5.0 s')

    act(() => root.render(<OperationTimingList status={{ ...status, state: 'succeeded', currentPhase: null, completedPhases: [{ ...status.currentPhase!, completedAt: '2026-09-11T00:00:05Z', elapsedMilliseconds: 4750 }] }} />))
    act(() => vi.advanceTimersByTime(10000))
    expect(host.querySelector('time')?.textContent).toBe('4.8 s')
    expect(vi.getTimerCount()).toBe(0)
  })
})
