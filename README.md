# QodexBot

QodexBot 将 QQ 机器人 C2C 单聊消息转发给本机 Codex CLI，并把 Codex 的回复、图片和桌面操作结果发送回 QQ。管理页面只监听本机地址，配置与会话记录保存在程序运行目录。

## 功能

- 通过 QQ 机器人开放平台接收和回复 C2C 单聊消息，支持正式环境与沙箱环境。
- 支持绑定多个 QQ 机器人；每个机器人使用独立的网关连接、Codex app-server、工作目录和会话列表。
- 在每个机器人内部为各 QQ 用户分别保存会话和上下文。
- 在 QQ 中通过可配置指令查看状态、管理个人会话、重试或停止任务，以及直接发送桌面截图。
- 将 Codex 生成的图片或本机图片直接发回 QQ。
- 向 Codex 提供截图、键鼠操作和向 QQ 用户提问等 MCP 工具。
- 提供本机管理页面和可分页查询的会话记录。
- 支持 Windows x64 Native AOT；运行发布包无需另行安装 .NET 运行时。

## 下载与运行

从 [GitHub Releases](https://github.com/RRQM/QodexBot/releases/latest) 下载最新的 `qodexbot-win-x64-aot-版本号.zip`，解压后运行 `QodexBot.exe`。首次启动会自动打开管理页面，默认地址为 <http://127.0.0.1:17890/>。

程序会在 `QodexBot.exe` 所在目录创建 `settings.json` 和 `conversations.jsonl`。请将程序解压到当前 Windows 用户可写入的目录。

## 首次配置

1. 打开管理页面，点击“添加机器人”，再为选中的机器人点击“扫码绑定 QQ”，使用手机 QQ 扫描并确认。可以重复添加并分别绑定多个机器人。
2. 选择正式或沙箱环境，并按平台要求开通 C2C 单聊权限、启用 `C2C_MESSAGE_CREATE` 事件订阅。可用场景取决于开发者主体和平台审批。
3. 扫码成功后，程序会在本机保存该机器人的 AppID 和 AppSecret，并自动连接机器人。管理页可以分别设置每个机器人的环境、消息订阅和 QQ 指令。修改管理端口后需要重启程序。

AppSecret 以明文保存在运行目录的 `settings.json` 中。管理页面不会读回已保存的密钥；修改其他设置时，AppSecret 留空即可保留现有值。请妥善保护程序目录和 Windows 用户账户。

管理服务默认只监听 `127.0.0.1`，不要将其代理或暴露到公网。

## 使用 Codex CLI

机器人和 Codex CLI 必须运行在同一台 Windows 电脑、同一个 Windows 用户环境下。该用户需要安装 Node.js，并通过 npm 全局安装、登录 Codex CLI：

```powershell
npm install -g @openai/codex
codex login
```

程序按当前用户的 npm 全局安装路径查找 `@openai/codex/bin/codex.js`，并通过 `node.exe` 启动 `codex app-server`。每个机器人收到 QQ 网关 READY 后会启动自己的 app-server；同一机器人内每位 QQ 用户有独立会话，不同机器人之间的 Codex 会话、MCP 工具通道和工作目录互相隔离。工作目录位于 Windows 临时目录下的 `QodexBot\bots\<机器人 ID>`。

## QQ 指令

在管理页面的“QQ 指令”区域可为每个动作设置命令文本，留空可禁用该动作。默认绑定如下：

| 默认命令 | 动作 |
| --- | --- |
| `\` | 显示指令列表 |
| `\状态` | 查看机器人和 Codex 状态 |
| `\会话` | 列出当前 QQ 用户自己的会话 |
| `\切换 2` | 切换到会话列表中的第 2 项 |
| `\新建 [名称]` | 创建并切换到新会话 |
| `\最近` | 查看最近的对话摘要 |
| `\当前` | 查看当前会话名称 |
| `\标题 新名称` | 重命名当前会话 |
| `\工作目录` | 查看 Codex 工作目录 |
| `\截图` | 直接截取桌面并发送到 QQ |
| `\重试` | 重发最近一次请求 |
| `\停止` | 中断当前 Codex 任务 |
| `\模型列表` | 查看当前 Codex CLI 提供的模型 |
| `\模型 2` | 按列表编号切换当前会话模型，也可直接输入模型名称 |
| `\更新Codex` | 更新全局 Codex CLI 并重启 app-server |

指令在机器人端直接处理，不会发给 Codex。普通消息仍会进入 Codex。每个机器人内的每位 QQ 用户有独立会话列表、当前会话和模型选择；会话选择保存在程序目录的 `sessions.json` 中。更新指令使用 npm 全局安装最新版 `@openai/codex`，并自动重启当前机器人的 app-server。

## QQ 和桌面 MCP 工具

程序通过本机随机命名管道连接 MCP 服务，不开放公网 MCP 端口。Codex 会话可以调用以下工具：

| 工具 | 用途 |
| --- | --- |
| `qq_capture_screen(processName?)` | 不传进程名时截取整个桌面；指定进程名（如 `Code`、`notepad`）时截取该进程面积最大的可见窗口，并发送到当前 QQ 单聊。 |
| `qq_send_image(path)` | 发送 `QodexBot\qq-outbox` 目录中的 PNG/JPEG 图片。 |
| `qq_ask_user(question)` | 向当前 QQ 用户提问，并等待其下一条单聊消息作为回答；等待时间为 5 分钟。 |
| `qq_input(action, ...)` | 对当前焦点窗口执行键盘输入或鼠标操作。支持 `type_text`、`press_key`、`click`、`move_mouse` 和 `scroll`。 |

截图和上传图片均受 QQ 的 10 MiB 图片限制。`qq_input` 使用虚拟屏幕绝对坐标；桌面截图和输入需要 Windows 交互桌面处于已登录、未锁屏状态。程序同一时间只执行一个 Codex 任务，并为最终答复预留 QQ 消息回复额度。

## 从源码运行和发布

开发环境需要 .NET 10 SDK、Node.js 和已登录的 Codex CLI。在仓库根目录运行：

```powershell
dotnet run --project src/QodexBot/QodexBot.csproj
```

QQ 扫码绑定由程序内的 C# 实现，不再需要额外安装 Node.js 扫码组件；Node.js 仍用于运行 Codex CLI。

运行测试：

```powershell
dotnet test src/QodexBot.slnx
```

截图测试会实际捕获 Windows 桌面，需要在已登录且未锁屏的交互桌面会话中运行。

版本号定义在 `src/Directory.Build.props`。将版本号更新后推送到 `main`，GitHub Actions 会比较推送前后的版本；版本有变化时，或该版本尚无 Release 时，自动发布 Windows x64 Native AOT 包并创建 GitHub Release。发布脚本也可在 Windows 上手动运行：

```powershell
./Publish-Packages.ps1 -NoPause
```

产物为 `artifacts/packages/qodexbot-win-x64-aot-版本号.zip`。

## 会话记录与日志

`conversations.jsonl` 会追加保存 QQ 用户消息、Codex 回复、推理摘要、提问与回答，以及图片发送事件；图片内容本身不会写入历史文件。管理页面支持按 QQ 用户 OpenID 筛选并分页查看记录。控制台会输出 QQ 消息、Codex 回复摘要和工具调用进度，请按敏感数据处理日志与会话文件。

## 常见问题

- **机器人 READY 但收不到消息：** 检查 C2C 单聊权限、`C2C_MESSAGE_CREATE` 订阅、机器人使用范围，以及 QQ 开放平台的 IP 白名单。
- **找不到 Codex CLI：** 确认当前 Windows 用户已安装并登录 Codex CLI，且 `node.exe` 可从 PATH 启动。
- **桌面工具无法工作：** 确认程序处于已登录、未锁屏的 Windows 交互桌面会话。
- **图片发送失败：** 确认文件是 PNG/JPEG，位于 `qq-outbox` 目录且不超过 10 MiB；平台权限和单条消息回复次数也会影响发送。

更多 QQ 平台说明见[腾讯 QQ 机器人文档](https://github.com/tencent-connect/bot-docs)。
