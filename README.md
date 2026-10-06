# Claude Codex Sync

**为 Claude + ChatGPT 双订阅用户准备的 Windows 会话同步工具。**

在 Claude Desktop 的 Code 会话里工作，达到 Claude 限额后，在 Codex 继续同一个会话；Claude 限额恢复后，再切回 Claude 官方账号。工具在两个客户端之间追加已保存的消息，保持上下文连续。

**Experimental / 实验版。** 自动捕获变化，目标释放后自动对齐。它不能让两个一直打开的官方窗口即时刷新，也不绕过正在使用的会话写入锁。

[下载 Windows 版本](https://github.com/Maxaccurate/claude-codex-sync/releases) · [English guide](docs/README.en.md) · [架构与贡献](CONTRIBUTING.md) · [MIT License](LICENSE)

## 它做什么

- Claude Desktop 官方账号 ↔ Codex / ChatGPT 桌面应用中的 **Code 会话**，双向追加可见消息和工具日志。
- 文件变化触发检测；等待完整轮次保存后同步，不依赖原生导入的定时更新。
- 两边都有新增时保留双方内容，来源标记与增量检查点防止反复复制。
- 按本机官方账号选择同步范围，只处理已经由 Codex 导入并存在映射的会话。
- 写入前保存文件备份；另外保存已捕获的增量保护日志。
- Codex 持有会话写入锁时排队；写入 Claude 前等待 Claude 正常退出。

这是一个本地 C# / WinForms 程序，没有 Python 常驻脚本或计划任务。默认同步窗口不代理模型请求，ChatGPT 订阅仍由 Codex 自己使用。

## 开始使用

### 准备

- Windows x64。
- 安装 Claude Desktop，在 **Code** 中创建并使用会话。
- 安装 Codex / 支持 Code 的 ChatGPT 桌面应用，登录自己的 ChatGPT 订阅。
- 在 Codex 中使用官方导入功能，先导入要接着工作的 Claude 会话。
- 本机有 Codex 自带的 app-server 或 Codex CLI；程序优先寻找 Microsoft Store 安装内的 `codex.exe`，也支持 npm 安装的 CLI。

### 下载并运行

1. 在 [Releases](https://github.com/Maxaccurate/claude-codex-sync/releases) 下载 Windows x64 ZIP，解压到一个自己有写入权限的目录。
2. 双击 `ClaudeLinkLite.exe`，选择窗口顶部的 Claude 官方账号。
3. 检查列表里的会话配对与待同步内容，再勾选 **开启自动对齐**。第一次默认关闭自动对齐。
4. 保持同步窗口打开，正常退出需要接收更新的目标应用，等待底部显示 **已对齐**。
5. 在目标应用打开同一个会话继续工作。下次切换时，也先结束当前生成、查看同步状态，再继续。

出现“等待”时，代表会话仍在使用或记录尚未保存完成。程序保留已捕获的内容并重试。不要把“已捕获”当成“目标已经更新”。

找不到配对时，确认 Claude Desktop 的 Code 会话已经在 Codex 导入，再点击 **重新扫描** / **刷新预览**。普通网页聊天没有这里所需的本地会话映射。

## 当前边界

- 支持已导入的本地 `legacy` 与 `paginated` 会话；未知格式、历史基底分支、缺失序号或导入边界会暂停处理。
- 目标正在使用时不会抢写。因此，这是一种 **捕获变化 + 安全时机追赶** 的同步，不保证两个官方客户端同时打开时的即时 UI 更新。
- 图片内容、加密推理、权限、凭证和工具执行状态不迁移。工具活动作为可读文字保留，不重新执行外来工具。
- 不会自动创建所有未配对的新会话，不传播删除，不强制结束官方应用。
- 官方应用更新或原生自动导入可能改写历史；检测到改写时会暂停，而不是用旧快照覆盖数据。两个写入工具同时管理同一历史时，应关注状态提示与备份。
- 此项目没有隶属于 Anthropic、OpenAI 或 CC Switch。

## 本地数据

程序在 EXE 同目录的 `Data/` 保存设置、检查点和备份。移动程序时一起保留该目录。

| 目录 / 文件 | 用途 |
| --- | --- |
| `Data/backups/transcript-bridge/` | 实际写入前的完整文件备份与路径清单 |
| `Data/realtime-journal/` | 已捕获的完整消息记录，保留源历史改写前的内容 |
| `Data/realtime-verification.json` | 尚待完成的 Codex 列表校验 |
| `Data/transcript-bridge.json` | 配对增量检查点 |

这些文件包含私人聊天内容。它们被 `.gitignore` 排除，不应作为 issue 附件或源码提交。安装包不包含账号、聊天或登录凭证。

## 从源码构建

需要 Windows 和 .NET 8 SDK。

```powershell
dotnet build src/ClaudeLinkLite/ClaudeLinkLite.csproj -c Release
dotnet publish src/ClaudeLinkLite/ClaudeLinkLite.csproj -c Release -r win-x64 --self-contained true -o dist/windows-x64
```

发布生成单文件 EXE，运行下载包无需另外安装 .NET SDK。

### 验证

```powershell
$report = Join-Path $PWD 'self-test-report.json'
$process = Start-Process -FilePath './src/ClaudeLinkLite/bin/Release/net8.0-windows/ClaudeLinkLite.exe' -ArgumentList @('--self-test', $report) -WindowStyle Hidden -Wait -PassThru
Get-Content $report
if ($process.ExitCode -ne 0) { throw 'Self-tests failed' }
```

测试使用临时目录和模拟服务，不应读取、同步或上传你的真实聊天。更深入的本地 app-server 兼容验证见 [贡献指南](CONTRIBUTING.md)。GitHub Actions 在 Windows 构建、运行隔离测试并打包。

## 开源来源

本项目采用 MIT 许可证。历史可选服务接入模块的认证和协议转换移植自 [CC Switch](https://github.com/farion1231/cc-switch)，增量桥接设计参考 [claudeimportfromcodex](https://github.com/FredrikAhman/claudeimportfromcodex)。第三方版权与许可证保留在 [THIRD-PARTY-NOTICES](src/ClaudeLinkLite/THIRD-PARTY-NOTICES.md) 和相邻 `LICENSE-*.txt` 中。

历史服务切换界面仍可通过 `--provider-settings` 打开；默认同步流程不需要它。
