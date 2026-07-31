# PLC AI Assistant

面向工业 PLC 工程的 AI 辅助工作台，首期支持 **Siemens TIA Portal V17**。它把程序理解、安全的源码编辑、版本控制和 AI 助手整合到一个本地桌面工作流中——同时始终不让 AI 直接、不受约束地访问你的 PLC 项目。

## 它能做什么

面对大型 PLC 项目，做任何修改之前都要先阅读成百上千的块、网络和变量表。PLC AI Assistant 把整个项目变成一棵可浏览、可查询、有版本管理的源码树：AI 助手基于它进行推理，你也可以像对待普通源代码一样浏览、编辑和回滚。

- **无需打开 TIA 即可阅读项目** — 块、网络、变量表和 UDT 只需导出一次并持久化保存，之后浏览、搜索、编辑和 AI 查询都可以完全离线进行。
- **用自然语言提问** — AI 助手基于知识图谱，针对真实的程序内容作答，而不是凭空猜测。
- **带护栏的编辑** — 每次写入 PLC 之前都要先预览、明确批准、校验并自动打快照。没有任何操作会悄悄改动在线项目。
- **保留完整历史** — 每个设备的源码基线都由 Git 管理，每次刷新和每次编辑都是一次可审查的提交，内置 diff、还原和分支功能。

## 核心概念

### 工作台项目

所有工作都在一个命名的**工作台（workbench）**中进行——它是一个目录，包含一个共享的 Git 仓库和一个或多个 worktree。每个 PLC 设备拥有独立的源码基线、记录你修改的稀疏 overlay，以及独立的知识库。多个 worktree 支持在不同分支上并行实验，并把结果合并回来。

### 离线优先，显式同步

已存储的基线与在线 PLC 之间只通过显式的、非破坏性的操作进行同步：**比较**把在线 PLC 导出到临时 staging 区域并展示差异；**批准**应用你选中的变更；**导入并编译**只把你选中的修改发送到 TIA。关闭 TIA 或重启应用都不会丢失基线、修改、历史记录或知识数据。

### 知识图谱

导出的源码会被摄取到每个设备独立的 SQLite 图谱中：块、网络、变量、交叉引用，以及翻译后的逻辑语句。助手和界面都基于这个图谱回答问题；界面还会实时显示其新鲜度（`missing` / `stale` / `current`），让你随时知道 AI 看到的是否是最新内容。

### 基于 MCP 的架构

每项能力都是一个独立的 [Model Context Protocol](https://modelcontextprotocol.io) 服务器，内置助手或任何兼容 MCP 的客户端都可以直接调用：

| 服务器 | 用途 |
|---|---|
| Engineering | TIA Portal 连接、导出/导入、编译（TIA Openness） |
| Knowledge | 源码摄取与图谱查询 |
| Source Editor | 受保护的解析、预览、应用、diff、校验 |
| Version Control | Git 状态、提交、diff、快照、还原、分支 |

### 安全设计

工具按风险分级，文件访问被限制在已登记的工作台根目录内，破坏性操作必须经过确认，所有操作都会记录到审计日志。工具参数和模型输出都无法自行扩大文件系统访问权限。

## 应用结构

- `studio/` — React + Vite 工作台界面
- `src/ApiHost/` — ASP.NET Core API，承载界面并桥接聊天与日志
- `src/Agent/` — AI 助手主循环（DeepSeek），带沙箱化的工具路由
- `src/Mcp.*` — 上表所述的各 MCP 服务器
- `src/Contracts/` — 共享契约与沙箱策略

## 运行前提

- Windows，已安装 Siemens TIA Portal V17 及 Openness API（用户需加入 "Siemens TIA Openness" 用户组）
- .NET Framework 4.8 与 .NET 8 SDK
- Node.js（用于 studio 界面）
- DeepSeek API key（供 AI 助手使用）

## 项目状态

活跃开发中。Engineering、Knowledge、Version Control 和聊天/助手部分已实现并有测试覆盖；安全的“生成 → 审查 → 应用”编辑工作流以及 Source Editor 的真机 TIA 验收仍在进行中。分阶段构建计划与当前里程碑状态见 `buildnote/plan/`。
