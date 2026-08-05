import { afterEach, describe, expect, it, vi } from 'vitest'
import * as api from '@/api/client'

describe('version-control workflow API sequence', () => {
  afterEach(() => vi.unstubAllGlobals())

  it('supports status, compare, import plan, import, validation, merge, and history in order', async () => {
    const calls: string[] = []
    vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL) => {
      const path = String(input)
      if (path.includes('/vc/status')) calls.push('status')
      else if (path.includes('/vc/compare-tia')) calls.push('compare-tia')
      else if (path.includes('/import-plans/')) calls.push('import')
      else if (path.includes('/import-plan')) calls.push('import-plan')
      else if (path.includes('/validate-merge')) calls.push('validate-merge')
      else if (path.includes('/validated-merges/')) calls.push('merge-validated')
      else if (path.includes('/vc/log')) calls.push('log')
      return new Response(JSON.stringify(path.includes('/vc/status') ? { repoPath: '', branch: 'feature-a', entries: [] } : path.includes('/vc/log') ? { repoPath: '', commits: [] } : path.includes('/import-plans/') ? { sessionId: 'session-1', objects: [] } : path.includes('/import-plan') ? { planId: 'plan-1', objects: [] } : path.includes('/validate-merge') ? { validationId: 'validation-1', state: 'Ready', devices: [] } : {}), { status: 200, headers: { 'Content-Type': 'application/json' } })
    }))

    await api.getWorktreeVcStatus('wb-1', 'wt-1')
    await api.compareMasterWithTia('wb-1')
    await api.planFeatureImport('wb-1', 'wt-1')
    await api.importFeaturePaths('wb-1', 'plan-1', ['devices/PLC_1/source/Blocks/A.xml'])
    await api.validateFeatureMerge('wb-1', 'wt-1', 'session-1', true, 'Studio user')
    await api.mergeValidatedFeature('wb-1', 'validation-1')
    await api.getWorktreeVcLog('wb-1', 'wt-1')

    expect(calls).toEqual(['status', 'compare-tia', 'import-plan', 'import', 'validate-merge', 'merge-validated', 'log'])
  })

  it('sends the required commit title when accepting TIA synchronization', async () => {
    let requestBody: { paths?: string[]; message?: string } | undefined
    vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      if (String(input).includes('/accept')) {
        requestBody = JSON.parse(String(init?.body)) as { paths?: string[]; message?: string }
      }
      return new Response(JSON.stringify({
        comparisonId: 'comparison-1',
        pendingPaths: [],
        commitSha: 'commit-2',
      }), { status: 200, headers: { 'Content-Type': 'application/json' } })
    }))

    await api.acceptTiaSynchronization(
      'wb-1',
      'comparison-1',
      ['devices/PLC_1/source/Blocks/Main.xml'],
      'Accept Main from TIA',
    )

    expect(requestBody).toEqual({
      paths: ['devices/PLC_1/source/Blocks/Main.xml'],
      message: 'Accept Main from TIA',
    })
  })
})
