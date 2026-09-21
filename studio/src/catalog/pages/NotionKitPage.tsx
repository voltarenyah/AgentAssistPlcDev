import { useState } from 'react'
import {
  Avatar,
  AvatarFallback,
  Badge,
  Button,
  Card,
  CardContent,
  CardDescription,
  CardHeader,
  CardTitle,
  Checkbox,
  Input,
  Label,
  Switch,
} from '@notion-kit/ui/primitives'

export default function NotionKitPage() {
  const [showCover, setShowCover] = useState(false)
  const [notifyAssignee, setNotifyAssignee] = useState(true)

  return (
    <div className="notion-kit-surface mx-auto max-w-5xl space-y-8" data-notion-kit-preview>
      <div>
        <p className="text-xs font-medium uppercase tracking-[0.16em] text-muted-foreground">Installed component source</p>
        <h2 className="mt-2 text-2xl font-semibold tracking-tight">Notion Kit components</h2>
        <p className="mt-2 max-w-2xl text-sm text-muted-foreground">
          Live previews from <code className="font-mono text-xs">@notion-kit/ui</code>. Use this page to compare the available
          primitives before selecting one for a Studio surface.
        </p>
      </div>

      <div className="grid gap-5 lg:grid-cols-2">
        <Card>
          <CardHeader>
            <CardTitle>Actions and status</CardTitle>
            <CardDescription>Choose a clear action weight and a compact status treatment.</CardDescription>
          </CardHeader>
          <CardContent className="p-6 pt-0 space-y-5">
            <div className="flex flex-wrap gap-2">
              <Button variant="primary" size="md">Create task</Button>
              <Button variant="soft-blue" size="md">Review source</Button>
              <Button variant="red" size="md">Discard draft</Button>
              <Button variant="link" size="md">Learn more</Button>
            </div>
            <div className="flex flex-wrap items-center gap-2">
              <Badge variant="blue">In review</Badge>
              <Badge variant="orange">Needs input</Badge>
              <Badge variant="gray">Draft</Badge>
              <Badge variant="tag">PLC-01</Badge>
            </div>
          </CardContent>
        </Card>

        <Card>
          <CardHeader>
            <CardTitle>Fields and people</CardTitle>
            <CardDescription>Use familiar form controls for focused engineering input.</CardDescription>
          </CardHeader>
          <CardContent className="p-6 pt-0 space-y-4">
            <div className="space-y-2">
              <Label htmlFor="notion-kit-task-name">Task name</Label>
              <Input id="notion-kit-task-name" placeholder="Describe the intended change" />
            </div>
            <div className="flex items-center gap-3">
              <Avatar aria-label="Assigned to Ansel">
                <AvatarFallback>AN</AvatarFallback>
              </Avatar>
              <div>
                <div className="text-sm font-medium">Ansel</div>
                <div className="text-xs text-muted-foreground">Assigned engineer</div>
              </div>
            </div>
          </CardContent>
        </Card>

        <Card>
          <CardHeader>
            <CardTitle>Preference controls</CardTitle>
            <CardDescription>These controls are live so you can inspect their selected states.</CardDescription>
          </CardHeader>
          <CardContent className="p-6 pt-0 space-y-4">
            <label className="flex items-center justify-between gap-4">
              <span>
                <span className="block text-sm font-medium">Show page cover</span>
                <span className="block text-xs text-muted-foreground">Display a cover above the task title.</span>
              </span>
              <Switch aria-label="Show page cover" checked={showCover} onCheckedChange={setShowCover} />
            </label>
            <label className="flex items-center gap-3 text-sm">
              <Checkbox checked={notifyAssignee} onCheckedChange={setNotifyAssignee} />
              Notify the assignee when this task changes
            </label>
          </CardContent>
        </Card>

        <Card asButton>
          <CardHeader>
            <CardTitle>Selectable card</CardTitle>
            <CardDescription>Use a card with button semantics when the whole item opens a detailed surface.</CardDescription>
          </CardHeader>
          <CardContent className="p-6 pt-0">
            <div className="flex items-center justify-between text-sm">
              <span>Source comparison</span>
              <Badge variant="blue">Ready</Badge>
            </div>
          </CardContent>
        </Card>
      </div>
    </div>
  )
}
