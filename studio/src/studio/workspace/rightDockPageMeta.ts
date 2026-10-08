import { Boxes, FileCheck2, History } from 'lucide-react'
import type { RightDockPage } from '@/studio/shellLayout'

/** One source for the rail's names and icons, so the rail and a page's header cannot disagree. */
export const RIGHT_DOCK_PAGE_LABELS: Record<RightDockPage, string> = {
  properties: 'Properties',
  changes: 'Changes',
  history: 'History',
}

export const RIGHT_DOCK_PAGE_ICONS: Record<RightDockPage, typeof Boxes> = {
  properties: Boxes,
  changes: FileCheck2,
  history: History,
}
