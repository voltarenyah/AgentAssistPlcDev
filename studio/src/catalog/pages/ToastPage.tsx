import { Button } from '@/components/ui/button'
import { toast } from 'sonner'

export default function ToastPage() {
  return (
    <div className="space-y-12">
      <div>
        <h2 className="mb-4 text-lg font-semibold">Sonner Toasts</h2>
        <div className="flex flex-wrap items-center gap-3">
          <Button
            variant="outline"
            onClick={() => toast('This is a default notification')}
          >
            Default Toast
          </Button>
          <Button
            variant="outline"
            onClick={() =>
              toast('This is a description', {
                description: 'With additional context below the title.',
              })
            }
          >
            With Description
          </Button>
          <Button
            variant="outline"
            onClick={() =>
              toast.success('Operation completed successfully!')
            }
          >
            Success Toast
          </Button>
          <Button
            variant="outline"
            onClick={() => toast.error('Something went wrong!')}
          >
            Error Toast
          </Button>
          <Button
            variant="outline"
            onClick={() =>
              toast.promise(new Promise((resolve) => setTimeout(resolve, 2000)), {
                loading: 'Loading...',
                success: 'Done!',
                error: 'Failed',
              })
            }
          >
            Promise Toast
          </Button>
          <Button
            variant="outline"
            onClick={() =>
              toast('Event has been created', {
                action: {
                  label: 'Undo',
                  onClick: () => toast('Action undone'),
                },
              })
            }
          >
            With Action
          </Button>
        </div>
      </div>
    </div>
  )
}
