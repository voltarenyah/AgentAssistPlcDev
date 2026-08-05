// @vitest-environment happy-dom
import React, { act } from 'react'
import { createRoot } from 'react-dom/client'
import { afterEach, describe, expect, it, vi } from 'vitest'
import type { ReconciliationPreview } from '@/api/client'
import RefreshDialog from './RefreshDialog'

globalThis.IS_REACT_ACT_ENVIRONMENT = true

const preview: ReconciliationPreview = {
  previewId: 'preview-1',
  worktreeId: 'master-1',
  deviceId: 'device-1',
  baselineTreeHash: 'old-tree',
  stagingTreeHash: 'new-tree',
  entries: [{
    relativePath: 'devices/PLC_1/source/Blocks/Main.xml',
    kind: 'Changed',
    baselineHash: 'old',
    stagingHash: 'new',
    componentIdentity: 'Main',
    storedFingerprints: 'old-fingerprint',
    liveFingerprints: 'new-fingerprint',
    fingerprintsMatch: false,
  }],
}

const render = async (onApply: (paths: string[], title?: string) => Promise<void>) => {
  const host = document.createElement('div')
  document.body.appendChild(host)
  const root = createRoot(host)
  await act(async () => root.render(
    <RefreshDialog
      preview={preview}
      busy={false}
      autoCommit
      onClose={vi.fn()}
      onApply={onApply}
    />,
  ))
  return { host, root }
}

const click = async (element: Element) => {
  await act(async () => element.dispatchEvent(new MouseEvent('click', { bubbles: true })))
}

const input = async (element: HTMLInputElement, value: string) => {
  await act(async () => {
    element.value = value
    element.dispatchEvent(new Event('input', { bubbles: true }))
    element.dispatchEvent(new Event('change', { bubbles: true }))
  })
}

afterEach(() => {
  vi.restoreAllMocks()
  document.body.innerHTML = ''
})

describe('RefreshDialog', () => {
  it('requires a TIA commit title before applying master changes', async () => {
    const onApply = vi.fn(async () => undefined)
    const { host } = await render(onApply)

    await click(host.querySelector('input[aria-label="Apply devices/PLC_1/source/Blocks/Main.xml"]')!)
    const apply = host.querySelector('button.primary-button') as HTMLButtonElement
    expect(apply.disabled).toBe(true)

    await input(host.querySelector('input[aria-label="TIA commit title"]')!, 'Accept Main from TIA')
    expect(apply.disabled).toBe(false)
    await click(apply)

    expect(onApply).toHaveBeenCalledWith(
      ['devices/PLC_1/source/Blocks/Main.xml'],
      'Accept Main from TIA',
    )
  })
})
