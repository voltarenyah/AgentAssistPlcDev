import { describe, expect, it } from 'vitest'
import { WorkbenchApiError } from '@/api/client'
import { shouldRetryOperationStatus } from './operationStatus'

describe('operation status polling', () => {
  it('keeps polling when the operation has not been registered yet', () => {
    expect(shouldRetryOperationStatus(new WorkbenchApiError(404, 'HTTP_404', 'Not found'))).toBe(true)
  })

  it('does not retry terminal API failures', () => {
    expect(shouldRetryOperationStatus(new WorkbenchApiError(500, 'HTTP_500', 'Server error'))).toBe(false)
    expect(shouldRetryOperationStatus(new Error('network failure'))).toBe(false)
  })
})
