# Automation Workbench 后端

后端以明确的“工作台项目”管理 PLC 工程源码。用户创建项目时提供名称，也可以自行选择根目录；未提供时默认使用：

```text
%LOCALAPPDATA%\AutomationWorkbench\Project\<workbench-name>
```

每个工作台包含一个共享的 bare Git 仓库，以及一个或多个完整的 linked worktree。每个工程设备拥有独立的 `device.json`、`exported-source` 基线、稀疏的 `modified-source`、`staging` 和 `plc-knowledge.db`。

`exported-source` 与 `modified-source` 由 Git 跟踪；`staging`、设备知识库和 `.automation` 会被忽略。全量导出先写入 staging，生成差异预览；只有用户确认后才更新真实基线并自动提交。未变化文件不会被重写，因此 Git 历史保持连续。

`modified-source` 只保留当前 worktree 实际修改、需要导回 PLC 的文件。导入和编译后 overlay 仍保留到该 worktree 生命周期结束。连续修改多个文件后，应在再次使用知识库前执行一次批量局部更新。

旧的 `%LOCALAPPDATA%\PlcAiAssistant\exports` 不迁移、不修改，也不会作为新工作台列出。当前范围仅包含后端；未来 UI 工作见 [UI 计划](buildnote/plan/workbench-project-storage-future-ui.md)。

