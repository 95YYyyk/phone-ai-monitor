# 开发记录

本文是发布版的开发概览，记录本修改版的架构、关键实现和限制。它不包含任何本机用户名、私有 IP、备份路径或具体对话标题。

## 与上游的关系

本项目基于 [kdwbll/desk-side-display](https://github.com/kdwbll/desk-side-display)，保留上游 MIT 许可证和版权声明。上游提供 Windows 控制面板、手机页面、USB/ADB 连接、歌词和硬件监控基础；本修改版在此基础上增加了本地化改进和 AI 任务看板，详见 `NOTICE.md` 与 `THIRD-PARTY-NOTICES.md`。

## 运行结构

- `Program.cs`：ASP.NET Core 入口，监听 8765（USB 显示页）、8766（控制面板）、8767（局域网显示页）。
- `Services/Display`：CPU/GPU/内存/网速、剪贴板、快捷键、便签和 Windows 窗口数据。
- `Services/Codex`：只读 Codex 会话 JSONL，提取任务开始/完成事件。
- `Services/Antigravity`：只读 Antigravity 的汇总 SQLite 和对话 SQLite，提取对话状态与 `update_steps` 任务清单。
- `Services/Media`：洛雪音乐、Apple Music、网易云音乐的播放状态和控制。
- `Services/Lyrics`：LRC/TTML/逐字歌词解析、匹配和校准。
- `wwwroot/display`：Android 手机显示页；`wwwroot/control`：Windows 控制面板；`wwwroot/shared`：共享组件和旧内核兼容层。

## 本修改版的实现要点

### 旧 Android 兼容

- `wwwroot/shared/compat.js` 补齐旧 WebView 缺失的 JS API。
- `wwwroot/display/legacy.css` 针对 `aspect-ratio`、`cqw/cqh`、`color-mix`、`-webkit-mask` 等旧内核不支持的 CSS 做降级。
- 在 Android 9 / Via 7.3.3 / WebView 80 上验证过歌词、监控、圆环和网速表。

### 监控页

- 顶部显示实时 `HH:MM:SS`。
- 四张仪表卡：CPU、内存、GPU 温度、网速；GPU 温度卡下方显示功耗和占用。
- 网速表以竖直直径为界，下载从 12 点、上传从 6 点逆时针绘制半圈，20 MB/s 满格；低于 1 MB/s 使用 KB/s。
- 网速采集排除 TAP、TUN、WireGuard、Tailscale、ZeroTier、Hyper-V、VMware、VirtualBox、Docker、WSL 等虚拟网卡。
- 已移除页面不再使用的 GPU 风扇和核心频率采集；`nvidia-smi` 只读取名称、占用、温度和功耗。

### AI 任务卡

- Codex：读取 `task_started` / `task_complete`，运行中显示“执行中”，完成后保留 2 分钟。
- Codex 启动恢复：首次只读文件尾部；尾部没有任务事件时，按 4 MB 分块向前回溯最近一次事件，避免服务重启后长对话消失。
- Antigravity：读取 `conversation_summaries.db`，状态 `RUNNING/BUSY/CANCELING` 视为运行中，`IDLE` 视为完成。
- Antigravity 子任务：只读取 `step_type=132` 的 `update_steps` JSON；比较清单 `idx` 和 `last_user_input_step_index`，避免新回合误显示上一回合的进度。
- SQLite 使用 `Mode=ReadOnly`、`PRAGMA query_only` 和短连接；不写 Antigravity 文件，不读取完整聊天正文。
- 两个来源合并到 `/api/tasks`；`/api/codex` 保留兼容，`/api/antigravity` 可用于单独查看。

### 其他

- 移除不使用的前端指标、v1/v2 组件 schema 和文档字段。
- CPU 温度助手默认关闭；需要时显式加 `--cpu-helper`。
- 源码模式使用 `UseAppHost=false`，不生成自定义 EXE；本仓库不提供预编译包。

## 构建与验证

```powershell
dotnet restore DeskSideDisplay.csproj
dotnet build DeskSideDisplay.csproj -c Release --nologo
```

启动后检查：

- `http://127.0.0.1:8765/` 返回显示页。
- `http://127.0.0.1:8766/` 返回控制面板。
- 局域网地址 `http://电脑IP:8767/` 可访问。
- `/api/tasks` 同时返回 Codex 与 Antigravity 任务（没有任务时为空数组）。
- 手机旧内核刷新后没有黑屏、圆环塌陷或任务卡溢出。

## 已知限制

- Codex 的 JSONL 和 Antigravity 的 SQLite 都是内部格式，应用升级可能改变字段；读取失败时应降级为空任务或上次快照，不能影响主服务。
- Antigravity 的任务清单只在其使用 `update_steps` 时存在；没有清单时只显示对话级状态和耗时。
- 局域网 8767 没有鉴权，只适合可信网络。
- 硬件读数依赖驱动和传感器；没有数据时显示 `—`。
- 本修改版只验证了 Windows 源码运行，没有重新打包 EXE。
