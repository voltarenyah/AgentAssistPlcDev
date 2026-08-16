import { WorkbenchApiError } from '@/api/client'

export const shouldRetryOperationStatus = (error: unknown) =>
  error instanceof WorkbenchApiError && error.status === 404
