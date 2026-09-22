// @vitest-environment happy-dom
import React, { act } from 'react'
import { createRoot } from 'react-dom/client'
import { afterEach, describe, expect, it, vi } from 'vitest'
import * as api from '@/api/client'
import FeatureValidationDialog from './FeatureValidationDialog'

globalThis.IS_REACT_ACT_ENVIRONMENT = true

const plan: api.FeatureImportPlan = {
  planId: 'plan-1',
  workbenchId: 'wb-1',
  featureWorktreeId: 'feature-1',
  featureSha: 'feature-sha',
  masterSha: 'master-sha',
  comparisonId: 'comparison-1',
  objects: [{
    deviceId: 'device-1',
    plcName: 'PLC_1',
    relativePath: 'devices/PLC_1/source/Blocks/Main.xml',
    featureFingerprint: 'fingerprint-1',
    importable: true,
    reason: null,
  }],
}

const render = async () => {
  const host = document.createElement('div')
  document.body.appendChild(host)
  const root = createRoot(host)
  await act(async () => root.render(
    <FeatureValidationDialog
      workbenchId="wb-1"
      featureWorktreeId="feature-1"
      plan={plan}
      onClose={vi.fn()}
    />,
  ))
  return { host, root }
}

const clickButton = async (host: HTMLElement, label: string) => {
  const button = [...host.querySelectorAll('button')].find(item => item.textContent?.includes(label))
  expect(button).toBeTruthy()
  await act(async () => button?.dispatchEvent(new MouseEvent('click', { bubbles: true })))
}

afterEach(() => {
  vi.restoreAllMocks()
  document.body.innerHTML = ''
})

describe('FeatureValidationDialog', () => {
  it('treats numeric Ready enum responses as completed validation', async () => {
    vi.spyOn(api, 'importFeaturePaths').mockResolvedValue({
      sessionId: 'session-1',
      planId: 'plan-1',
      featureSha: 'feature-sha',
      masterSha: 'master-sha',
      startedAt: 'now',
      objects: [{
        deviceId: 'device-1',
        relativePath: plan.objects[0].relativePath,
        state: 'Imported',
        error: null,
        warnings: [],
      }],
    })
    vi.spyOn(api, 'validateFeatureMerge').mockResolvedValue({
      validationId: 'validation-1',
      state: 0,
      error: null,
      devices: [],
    })

    const { host } = await render()
    await clickButton(host, 'Import selected')
    const machineValidation = host.querySelector<HTMLInputElement>('input[aria-label="Machine validation completed"]')!
    await act(async () => {
      machineValidation.dispatchEvent(new MouseEvent('click', { bubbles: true }))
    })
    await clickButton(host, 'Compile all devices')

    expect(host.textContent).toContain('Validation: Ready')
    expect([...host.querySelectorAll('button')].some(item => item.textContent?.includes('Merge validated feature'))).toBe(true)
  })
})
