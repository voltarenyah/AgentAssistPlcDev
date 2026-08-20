import { describe, expect, it } from 'vitest'
import { WorkbenchApiError } from '@/api/client'
import { parseExportProgress, shouldRetryOperationStatus } from './operationStatus'

describe('operation status polling', () => {
  it('keeps polling when the operation has not been registered yet', () => {
    expect(shouldRetryOperationStatus(new WorkbenchApiError(404, 'HTTP_404', 'Not found'))).toBe(true)
  })

  it('does not retry terminal API failures', () => {
    expect(shouldRetryOperationStatus(new WorkbenchApiError(500, 'HTTP_500', 'Server error'))).toBe(false)
    expect(shouldRetryOperationStatus(new Error('network failure'))).toBe(false)
  })
})

describe('parseExportProgress', () => {
  it('extracts current and total from the cumulative export counter message', () => {
    expect(parseExportProgress('Exported PLC source files: 120 of 340')).toEqual({ current: 120, total: 340 })
  })

  it('returns null for counter messages without a total and for unrelated messages', () => {
    expect(parseExportProgress('Exported PLC source files: 120')).toBeNull()
    expect(parseExportProgress('Exporting block Main_OB1...')).toBeNull()
    expect(parseExportProgress('Exported PLC source files: 0 of 0')).toBeNull()
    expect(parseExportProgress(null)).toBeNull()
    expect(parseExportProgress(undefined)).toBeNull()
  })
})
