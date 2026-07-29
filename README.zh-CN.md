# Automation Workbench 后端

后端以明确的“工作台项目”管理 PLC 工程源码。用户创建项目时提供名称，也可以自行选择根目录；未提供时默认使用：

```text
%LOCALAPPDATA%\AutomationWorkbench\Project\<workbench-name>
```

每个工作台包含一个共享的 bare Git 仓库，以及一个或多个完整的 linked worktree。每个工程设备拥有独立的 `device.json`、`exported-source` 基线、稀疏的 `modified-source`、`staging` 和 `plc-knowledge.db`。

`exported-source` 与 `modified-source` 由 Git 跟踪；`staging`、设备知识库和 `.automation` 会被忽略。全量导出先写入 staging，生成差异预览；只有用户确认后才更新真实基线并自动提交。未变化文件不会被重写，因此 Git 历史保持连续。

`modified-source` 只保留当前 worktree 实际修改、需要导回 PLC 的文件。导入和编译后 overlay 仍保留到该 worktree 生命周期结束。连续修改多个文件后，应在再次使用知识库前执行一次批量局部更新。

## 离线工作与 TIA 同步

日常的块浏览、overlay 编辑、Git 操作和知识查询都使用已经持久化的设备文件，不需要打开 TIA Portal。关闭 TIA 或重启应用不会清除 `exported-source`、`modified-source`、Git 历史或 `plc-knowledge.db`。块索引由 Git 跟踪的 `exported-source/metadata.json` 重建，并与稀疏 overlay 合并。

设备概览根据磁盘文件和 `device.json` 实时显示知识状态：

- `missing`：`plc-knowledge.db` 不存在。
- `stale`：数据库存在，但持久化元数据表明基线或 overlay 有尚未摄取的变化。
- `current`：数据库存在，且持久化元数据中没有过期标记。

执行明确的 **Compare with TIA（与 TIA 比较）** 或 **Import & compile（导入并编译）** 前，先使用 **Open project in TIA（在 TIA 中打开项目）**。比较操作只把在线 PLC 导出到临时 `staging`，并显示已存储与在线指纹；该过程不会修改 Git 跟踪的基线、overlay、Git 历史或知识库。只有工程师明确批准所选基线变化后，才会应用这些变化。导入并编译同样是明确操作，只会把选中的修改源码发送到 TIA。

旧的 `%LOCALAPPDATA%\PlcAiAssistant\exports` 不迁移、不修改，也不会作为新工作台列出。

用户选择自定义根目录时，可信后端会校验持久化的 workbench 元数据，并把规范化且不经过 reparse point 的根目录登记到 `%APPDATA%\AutomationWorkbench\trusted-workbench-roots.json`。Engineering 与 Source Editor 子进程只接收该可信登记文件的位置；普通工具参数不能自行扩大允许目录。未登记目录和 reparse point 仍会被 sandbox 拒绝。
