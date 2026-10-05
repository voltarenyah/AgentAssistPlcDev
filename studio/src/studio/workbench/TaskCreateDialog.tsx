import { useEffect, useRef, useState } from 'react'
import { Loader2 } from 'lucide-react'
import * as api from '@/api/client'
import { showErrorToast } from '@/components/ui/toast'
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from '@/components/ui/dialog'

type Props = {
  workbenchId: string
  worktreeId: string
  open: boolean
  /**
   * The target the create action was invoked from — the navigator's `TASKS` section passes the target
   * it is showing. It is applied when the dialog opens, so creation starts pre-bound (AC-005).
   */
  origin?: api.TaskTarget | null
  /** The worktree's devices, as the owner of the surface already knows them: the select's options. */
  devices?: api.DeviceSummary[]
  onClose: () => void
  onCreated: () => void
}

const displayError = (error: unknown) => {
  if (error instanceof api.WorkbenchApiError) return `${error.code}: ${error.message}`
  return error instanceof Error ? error.message : 'Unexpected operation failure'
}

/** The target select's sentinel for the worktree's hardware configuration, which has no device id. */
const HARDWARE_TARGET = 'hardware'

/**
 * Creates one task for a worktree, already bound to the target the action came from. The shell owns
 * this dialog, like the workbench and worktree creation dialogs, so the navigator's create action
 * opens it over whatever the main area is showing instead of navigating that area away.
 */
export default function TaskCreateDialog({
  workbenchId,
  worktreeId,
  open,
  origin = null,
  devices = [],
  onClose,
  onCreated,
}: Props) {
  const [title, setTitle] = useState('')
  const [intent, setIntent] = useState('')
  const [expectedResult, setExpectedResult] = useState('')
  const [type, setType] = useState<api.EngineeringTask['type']>('feature')
  const [device, setDevice] = useState('')
  const [targetKind, setTargetKind] = useState<api.EngineeringTaskTargetKind>('device')
  const [adding, setAdding] = useState(false)
  const appliedOrigin = useRef(false)

  // The origin is read as values, not as an object, and it is applied once per opening: a target the
  // user picks inside the dialog is never overwritten while the dialog stays open.
  const originKind = origin?.kind ?? null
  const originDeviceId = origin?.kind === 'device' ? origin.deviceId : null
  useEffect(() => {
    if (!open) {
      appliedOrigin.current = false
      return
    }
    if (appliedOrigin.current) return
    appliedOrigin.current = true
    setTargetKind(originKind === 'hardware' ? 'hardware' : 'device')
    setDevice(originDeviceId ?? '')
  }, [open, originKind, originDeviceId])

  const availableDevices = devices

  const addTask = () => {
    const trimmedTitle = title.trim()
    const trimmedIntent = intent.trim()
    const trimmedResult = expectedResult.trim()
    const hardwareTarget = targetKind === 'hardware'
    // A hardware task is the one target that needs no device; every other task still needs one.
    if (!trimmedTitle || !trimmedIntent || !trimmedResult || adding || (!hardwareTarget && !device)) return
    setAdding(true)
    void api.createGraphWorktreeTask(workbenchId, worktreeId, hardwareTarget
      ? { title: trimmedTitle, targetKind: 'hardware', type, intent: trimmedIntent, expectedResult: trimmedResult }
      : { title: trimmedTitle, deviceId: device, type, intent: trimmedIntent, expectedResult: trimmedResult })
      .then(() => {
        setTitle('')
        setIntent('')
        setExpectedResult('')
        setType('feature')
        setDevice('')
        setTargetKind('device')
        onCreated()
        onClose()
      })
      .catch(addError => showErrorToast(`Task could not be created: ${displayError(addError)}`))
      .finally(() => setAdding(false))
  }

  return (
    <Dialog open={open} onOpenChange={next => { if (!next) onClose() }}>
      <DialogContent>
        <DialogHeader>
          <DialogTitle>Add task</DialogTitle>
          <DialogDescription>Create a focused task for this worktree.</DialogDescription>
        </DialogHeader>
        <form className="space-y-3" onSubmit={event => { event.preventDefault(); addTask() }}>
          <label className="field-label"><span>Title</span><input autoFocus aria-label="New task title" className="field-input" value={title} onChange={event => setTitle(event.target.value)} /></label>
          <label className="field-label"><span>Type</span><select aria-label="New task type" className="field-input" value={type} onChange={event => setType(event.target.value as api.EngineeringTask['type'])}><option value="issue">Issue</option><option value="improvement">Improvement</option><option value="feature">Feature</option></select><span className="text-[9px] text-muted-foreground">Saved with the task’s modification plan.</span></label>
          <label className="field-label"><span>Target</span><select required aria-label="New task target" className="field-input" value={targetKind === 'hardware' ? HARDWARE_TARGET : device} onChange={event => { const value = event.target.value; if (value === HARDWARE_TARGET) { setTargetKind('hardware'); setDevice(''); return } setTargetKind('device'); setDevice(value) }}><option value="">Select a device or the hardware configuration</option><option value={HARDWARE_TARGET}>Hardware configuration</option>{availableDevices.map(available => <option key={available.deviceId} value={available.deviceId}>{available.plcName || 'Unnamed PLC'}</option>)}</select><span className="text-[9px] text-muted-foreground">{targetKind === 'hardware' ? 'A hardware task covers this worktree’s hardware configuration and binds no PLC device.' : 'A task belongs to exactly one device. Add source objects from the task detail after creation.'}</span></label>
          <label className="field-label"><span>Goal</span><input required aria-label="New task goal" className="field-input" value={intent} onChange={event => setIntent(event.target.value)} placeholder="What should change?" /></label>
          <label className="field-label"><span>Expected result</span><textarea required aria-label="New task expected result" className="field-input min-h-16" value={expectedResult} onChange={event => setExpectedResult(event.target.value)} placeholder="How will you know it is done?" /></label>
          <DialogFooter><button type="button" className="secondary-button" onClick={onClose} disabled={adding}>Cancel</button><button type="submit" className="primary-button" disabled={!title.trim() || !intent.trim() || !expectedResult.trim() || (targetKind !== 'hardware' && !device) || adding}>{adding && <Loader2 className="h-3.5 w-3.5 animate-spin" />} Create task</button></DialogFooter>
        </form>
      </DialogContent>
    </Dialog>
  )
}
