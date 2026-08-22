import { WorkbenchApiError } from '@/api/client'

export const shouldRetryOperationStatus = (error: unknown) =>
  error instanceof WorkbenchApiError && error.status === 404

export type ExportProgress = { current: number; total: number }

/**
 * Extracts the cumulative "current of total" counts the backend adds to the export
 * counter message ("Exported PLC source files: 120 of 340") during a full compare.
 * Returns null for plain counter messages without a known total.
 */
export const parseExportProgress = (message: string | null | undefined): ExportProgress | null => {
  const match = message?.match(/^Exported PLC source files: (\d+) of (\d+)$/)
  if (!match) return null
  const current = Number(match[1])
  const total = Number(match[2])
  return total > 0 ? { current, total } : null
}
