// @vitest-environment happy-dom
import React, { act } from 'react'
import { createRoot } from 'react-dom/client'
import { afterEach, describe, expect, it } from 'vitest'
import * as api from '@/api/client'
import DevicePropertiesDock from './DevicePropertiesDock'

globalThis.IS_REACT_ACT_ENVIRONMENT = true

const meta: api.DeviceExportMetadata = {
  plcName: 'PLC_1',
  deviceName: 'CPU 1516',
  typeIdentifier: 'OrderNumber:6ES7 516-3AN02-0AB0',
  projectName: 'TestPLCExportDemo',
  projectAuthor: 'Ansel',
  projectComment: null,
  projectVersion: '1.0',
  projectCopyright: null,
  projectCreationTime: null,
  projectLastModified: null,
  projectLastModifiedBy: null,
  isSafetyDevice: null,
  fSignatureReadState: null,
  fSignature: null,
}

const info = {
  deviceId: 'dev1',
  plcName: 'PLC_1',
  engineeringIdentity: 'eng-1',
  sourceRoot: 'C:/wb/source',
  knowledgeDbPath: 'C:/wb/plc-knowledge.db',
  sourceProjectPath: 'C:/tia/TestPLCExportDemo.ap17',
} as api.DeviceInfo

const render = async (element: React.ReactNode) => {
  const host = document.createElement('div')
  document.body.appendChild(host)
  const root = createRoot(host)
  await act(async () => root.render(element))
  return { host, root }
}

afterEach(() => {
  document.body.innerHTML = ''
})

describe('DevicePropertiesDock', () => {
  it('shows an empty hint before the snapshot loads', async () => {
    const { host } = await render(<DevicePropertiesDock meta={null} info={null} hidden={false} />)

    expect(host.textContent).toContain('Device details appear once the snapshot loads')
  })

  it('lists TIA project properties and paths', async () => {
    const { host } = await render(<DevicePropertiesDock meta={meta} info={info} hidden={false} />)

    expect(host.textContent).toContain('TestPLCExportDemo')
    expect(host.textContent).toContain('Ansel')
    expect(host.textContent).toContain('6ES7 516-3AN02-0AB0')
    expect(host.textContent).toContain('C:/tia/TestPLCExportDemo.ap17')
    expect(host.textContent).toContain('C:/wb/plc-knowledge.db')
  })

  it('shows one PLC source path without baseline or overlay properties', async () => {
    const { host } = await render(<DevicePropertiesDock meta={meta} info={info} hidden={false} />)

    const plcSourceLabels = Array.from(host.querySelectorAll('div'))
      .filter(element => element.textContent === 'PLC source')
    expect(plcSourceLabels).toHaveLength(1)
    expect(host.textContent).toContain('C:/wb/source')
    expect(host.textContent).not.toContain('Exported baseline')
    expect(host.textContent).not.toContain('Modified overlay')
  })

  it('groups device-level metadata into its own section', async () => {
    const { host } = await render(<DevicePropertiesDock meta={meta} info={info} hidden={false} />)

    expect(host.textContent).toContain('Device')
    expect(host.textContent).toContain('PLC_1')
    expect(host.textContent).toContain('CPU 1516')
  })

  it('shows the project comment under TIA project properties', async () => {
    const { host } = await render(
      <DevicePropertiesDock meta={{ ...meta, projectComment: 'Line 1\nLine 2' }} info={info} hidden={false} />,
    )

    expect(host.textContent).toContain('Project comment')
    expect(host.textContent).toContain('Line 1')
    expect(host.textContent).toContain('Line 2')
  })

  it('omits properties that have no value', async () => {
    const { host } = await render(<DevicePropertiesDock meta={meta} info={info} hidden={false} />)

    expect(host.textContent).not.toContain('Copyright')
    expect(host.textContent).not.toContain('Project comment')
  })

  it('shows failsafe state and F-signature for a safety device', async () => {
    const { host } = await render(
      <DevicePropertiesDock
        meta={{ ...meta, isSafetyDevice: true, fSignatureReadState: 'ok', fSignature: '1A2B3C4D' }}
        info={info}
        hidden={false}
      />,
    )

    expect(host.querySelector('[data-testid="device-safety-section"]')).not.toBeNull()
    expect(host.textContent).toContain('Failsafe device')
    expect(host.textContent).toContain('Yes')
    expect(host.textContent).toContain('OK')
    expect(host.textContent).toContain('1A2B3C4D')
  })

  it('explains a license-gated F-signature read state', async () => {
    const { host } = await render(
      <DevicePropertiesDock
        meta={{ ...meta, isSafetyDevice: true, fSignatureReadState: 'no-signature' }}
        info={info}
        hidden={false}
      />,
    )

    const section = host.querySelector('[data-testid="device-safety-section"]')
    expect(section).not.toBeNull()
    expect(section!.textContent).toContain('No signature (Safety license required)')
    expect(section!.textContent).not.toContain('1A2B3C4D')
  })

  it('marks an ordinary PLC as not failsafe', async () => {
    const { host } = await render(
      <DevicePropertiesDock meta={{ ...meta, isSafetyDevice: false }} info={info} hidden={false} />,
    )

    const section = host.querySelector('[data-testid="device-safety-section"]')
    expect(section).not.toBeNull()
    expect(section!.textContent).toContain('No')
    expect(section!.textContent).not.toContain('F-signature state')
  })

  it('hides the safety section for legacy manifests without safety fields', async () => {
    const { host } = await render(<DevicePropertiesDock meta={meta} info={info} hidden={false} />)

    expect(host.querySelector('[data-testid="device-safety-section"]')).toBeNull()
  })
})
