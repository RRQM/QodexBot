# QodexBot

这个 .NET 10 控制台程序通过 QQ 机器人开放平台接收 C2C 单聊消息，把消息交给本机 Codex CLI，再将 Codex 的回答发回 QQ。配置由基于 TouchSocket.Http 的本机管理页面维护，并保存为 JSON。

## 准备机器人

1. 在 [QQ 机器人开放平台](https://q.qq.com/) 创建机器人，获取 AppID 和 AppSecret。
2. 确认机器人拥有消息列表单聊（C2C）权限，并在事件订阅中启用 `C2C_MESSAGE_CREATE`。可用场景取决于开发者主体和平台审批。
3. 在管理页面填写 AppID 和 AppSecret；不再需要编辑源码里的凭据。

## 运行

在项目目录运行：

```powershell
dotnet run
```

启动后程序会自动打开管理页面。管理服务默认使用 `http://127.0.0.1:17890/`，只监听本机。页面可配置 AppID、AppSecret、正式/沙箱环境、C2C 消息订阅和管理端口。所有持久化设置统一写入当前 Windows 用户的本地应用数据目录下的 `QodexBot\settings.json`；机器人设置保存后自动应用，管理端口变更需重启程序。

AppSecret 以明文保存在当前 Windows 用户的本地 JSON 配置文件中，请确保 Windows 账户安全。管理页读取配置时不会返回已保存的 AppSecret；更新其他设置时，留空即可保留现有密钥。

## 测试

运行 `dotnet test src/QodexBot.slnx`。截图测试会实际捕获当前 Windows 桌面并校验 PNG，因此需要在已登录且未锁屏的交互桌面会话中运行。

程序默认使用正式环境；在管理页面切换沙箱后会使用相应环境和 C2C Intent。管理端口等设置不会从环境变量读取。

## Codex CLI 转发

机器人与 Codex CLI 必须在同一台 Windows 用户环境下运行，并确认该 Windows 用户已经登录 Codex CLI。QQ 机器人收到 READY 后会启动一个常驻 `codex app-server` 进程，创建一个 Codex 会话；之后每条单聊消息都在这个会话中继续，保留前文上下文。进程在机器人退出时才关闭。

CLI 程序位置默认按当前 Windows 用户的 npm 全局安装路径查找：`AppData\Roaming\npm\node_modules\@openai\codex\bin\codex.js`，并通过 `node.exe` 启动。Codex 会话的工作目录位于 Windows 临时目录下的 `QodexBot` 文件夹。

QQ 接入部分自动获取和刷新 `access_token`、连接 WebSocket 网关、发送心跳并在断开后重连。回复依赖平台的 C2C 权限、事件订阅、机器人使用范围和回复时限；正式环境 IP 白名单也必须符合平台配置。

如果程序显示 READY 但收不到单聊消息，检查 C2C 权限、`C2C_MESSAGE_CREATE` 事件订阅、机器人使用范围和 QQ 开放平台的 IP 白名单配置。官方文档：[QQ 机器人接入说明](https://github.com/tencent-connect/bot-docs)。

## 将 Codex 生成的图片发回 QQ

程序会从 Codex app-server 的 `imageGeneration` 完成事件读取 PNG/JPEG 图片，并在同一条 C2C 回复中发送文字和图片。图片最大 10 MiB，每次回复最多发送 4 张图片；文本和图片消息共用 QQ 对单条消息的回复次数限制。

程序通过 QQ `/files` 接口的 `file_data` 字段直接上传 Base64 图片，再以 `msg_type=7` 发送，不需要公网 HTTPS 图片地址或反向代理。图片最大 10 MiB，每次回复最多发送 4 张图片；如果 QQ 接口拒绝直接上传，日志会记录失败原因。

## QQ 图片与交互 MCP 工具

Bot 会在本机启动一个仅通过随机命名管道访问的 MCP 服务，并把它配置给 Codex app-server；不会监听公网端口。会话内提供两个工具：

- `qq_send_image(path)`：把 Windows 临时目录下 `QodexBot\qq-outbox` 中的 PNG/JPEG 图片直接上传并发送到当前 QQ 单聊。图片须小于或等于 10 MiB。Codex 可先把截图或其他图片写入该目录，再调用此工具。
- `qq_capture_screen(processName?)`：不传参数时捕获整个 Windows 桌面；指定进程名（例如 `Code`、`notepad`）时，截取该进程面积最大的可见窗口并直接发送到当前 QQ 单聊。
- `qq_ask_user(question)`：把问题发给当前 QQ 用户，然后等待该用户的下一条单聊消息作为答案，Codex 收到答案后继续当前任务。等待时间为 5 分钟。

为保证提问期间还能接收回答，QQ 网关收消息和 Codex 任务采用并行处理，但同一时间只执行一个 Codex 任务。QQ 对单条用户消息的机器人回复最多 5 条；Bot 会为任务最终答复保留一条回复额度。

## 运行日志

控制台会完整打印 QQ 输入和补充回答、Codex 推理摘要与最终输出，以及 app-server 报告的工具调用/结果和 MCP 工具进度。推理摘要设置为 `detailed`；Codex 不会通过该接口提供原始内部思维链，因此日志记录的是 app-server 暴露的摘要内容。

管理页的“会话记录”会显示所有 QQ 用户的消息、Codex 回复、推理摘要、提问/回答和图片发送事件，可按用户 OpenID 筛选并分页查看。历史记录追加保存在与设置相同目录的 `QodexBot\conversations.jsonl`，程序重启后仍可浏览；图片内容本身不会写入历史文件，只记录图片数量或发送事件。
