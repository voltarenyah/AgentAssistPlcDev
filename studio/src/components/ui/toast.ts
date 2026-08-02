import { toast } from 'sonner'

const copyTextToClipboard = async (message: string) => {
  if (typeof navigator !== 'undefined' && navigator.clipboard?.writeText) {
    try {
      await navigator.clipboard.writeText(message)
      return
    } catch {
      // Fall back to the legacy API when clipboard permissions are unavailable.
    }
  }

  const textarea = document.createElement('textarea')
  textarea.value = message
  textarea.setAttribute('readonly', '')
  textarea.style.position = 'fixed'
  textarea.style.opacity = '0'
  document.body.appendChild(textarea)
  textarea.select()
  try {
    document.execCommand('copy')
  } finally {
    textarea.remove()
  }
}

export const showErrorToast = (message: string) => {
  toast.error(message, {
    action: {
      label: 'Copy error',
      onClick: () => { void copyTextToClipboard(message) },
    },
  })
}
