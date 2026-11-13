# CodeBuddy 工作套件

把本地开发环境（项目索引库 / 运行看板 / 内存降温 / 脚本库）打包成 **Windows 一键安装包**。
目标机解压后双击 `installer.exe` 即可，**不需要预装 Node、Git 或浏览器**。

```
下载 CodeBuddyWorkKit-1.0.0.zip  →  解压  →  双击 installer.exe  →  完成
```

安装器会先做环境检测并说明将安装什么，**确认后**才开始；装完自动跑一轮自检（18 项），全通过才显示「部署完成」。

---

## 包含什么

| 组件 | 用途 |
|---|---|
| **CodeVault** 索引库 | 本机所有项目的构建逻辑、分区职责、已知问题，网页浏览 |
| **Pulse** 运行看板 | 工具调用与任务进度采集；**重复测试进程守护**（≥4 告警、≥6 锁死并清理） |
| **Tuner** 内存降温 | 工作集回收 + 系统缓存清理（待机页表，需管理员） |
| **ScriptVault** 脚本库 | 8 个可复用工具 + 3 个 Python 模块，关键词检索后直接执行 |

## 两种窗口形态

| | 原生窗口（默认） | 浏览器嵌入版 |
|---|---|---|
| 程序 | `CodeBuddyWorkKit.Native.exe` | `CodeBuddyWorkKit.Shell.exe` |
| 渲染 | 纯 WinForms 自绘 | Edge `--app` 窗口嵌入 |
| 依赖 | **零依赖** | 需要 Edge |
| 体积 | 55 KB | 47 KB |

两者都：最小化/关闭收进托盘、双击托盘恢复、**不做开机自启**。

原生窗口带报错面板（平时灰、有错变红并显示数量）、设置面板（端口/监听地址）、
运行时健康自检（连续 3 次探测失败记错误）、CPU/内存趋势图。

## 关键设计

**不做开机自启。** 不写注册表 `Run`、不放启动文件夹，只有手动双击快捷方式才运行。

**一键回退。** 部署目录里 `一键清理与回退.cmd` 会停服务、删快捷方式、清注册表、移除规则、删目录。

**版本可检测。** 安装前会读注册表与 `deploy.json`，告诉你是「首次安装 / 同版本重装 / 升级 / 降级」，
并显示已装版本和部署日期。

**多平台规则分发。** 同一份规则正文写入 CodeBuddy / Cursor / Windsurf（独占文件）与
Claude Code / Codex / Cline（受管区块），卸载时只删自己那一块。

## 目录结构

```
installer.exe              安装器
payload/
  apps/                    四个应用
  native/                  原生窗口（纯 WinForms）
  shell/                   浏览器嵌入版窗口
  tools/                   部署与运维脚本（检测/部署/自检/回退/规则分发）
  rules/                   目标机 AI 规则模板
  runtime/                 便携 node.exe + MinGit
  docs/                    使用说明、更新日志
  VERSION                  版本号（唯一来源）
```

## 开发

改完代码重新打包：

```powershell
powershell -File build\build-package.ps1 -Out zip\CodeBuddyWorkKit-1.0.0.zip
```

升版本（同时改 `VERSION` 与 `CHANGELOG.md`）：

```powershell
powershell -File build\bump-version.ps1 -Kind minor -Note "新增 XX 能力"
```

详细约定见 [`AGENTS.md`](AGENTS.md)（目录地图、改什么改哪里、踩过的坑）与 [`CHANGELOG.md`](CHANGELOG.md)。

## 许可

MIT，见 [`LICENSE`](LICENSE)。

> 随包分发的 Node.js 与 MinGit 遵循各自许可（Node.js MIT、Git GPL-2.0）。
