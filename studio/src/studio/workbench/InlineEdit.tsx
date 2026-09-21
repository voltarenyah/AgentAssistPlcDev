import { useEffect, useRef, useState } from 'react'
import { Input, Textarea } from '@notion-kit/ui/primitives'

type Props = {
  value: string
  placeholder: string
  ariaLabel: string
  multiline?: boolean
  disabled?: boolean
  onSave: (value: string) => Promise<void> | void
}

/**
 * Plain-text inline editor: shows the current value, saves on blur or Enter
 * (Ctrl+Enter for multiline) when the text actually changed. Escape reverts.
 */
export default function InlineEdit({ value, placeholder, ariaLabel, multiline, disabled, onSave }: Props) {
  const [draft, setDraft] = useState(value)
  const [saving, setSaving] = useState(false)
  const dirtyRef = useRef(false)

  useEffect(() => {
    setDraft(value)
    dirtyRef.current = false
  }, [value])

  const save = async () => {
    if (!dirtyRef.current || saving) return
    dirtyRef.current = false
    setSaving(true)
    try {
      await onSave(draft.trim())
    } finally {
      setSaving(false)
    }
  }

  const sharedProps = {
    'aria-label': ariaLabel,
    className: `w-full text-[10px]! ${multiline ? 'min-h-[64px] resize-y py-1.5' : 'h-7!'}`,
    placeholder,
    value: draft,
    disabled: disabled || saving,
    onChange: (event: React.ChangeEvent<HTMLInputElement | HTMLTextAreaElement>) => {
      dirtyRef.current = true
      setDraft(event.target.value)
    },
    onBlur: () => void save(),
    onKeyDown: (event: React.KeyboardEvent<HTMLInputElement | HTMLTextAreaElement>) => {
      if (event.key === 'Escape') {
        dirtyRef.current = false
        setDraft(value)
        return
      }
      const commit = multiline ? event.key === 'Enter' && (event.ctrlKey || event.metaKey) : event.key === 'Enter'
      if (commit) {
        event.preventDefault()
        void save()
      }
    },
  }

  return multiline
    ? <Textarea {...sharedProps} />
    : <Input type="text" {...sharedProps} />
}
