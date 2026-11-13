# 更新日志

本文件记录**对外可见**的版本变化。版本号的唯一来源是 `payload/VERSION`，
两者必须同步 —— 用 `build/bump-version.ps1` 一次改好，不要手改其中一处。

## 版本号规则

| 变化 | 位数 | 例子 |
|---|---|---|
| 修 bug、改文案、调参数 | PATCH | 1.0.0 → 1.0.1 |
| **新增功能**、大范围优化 | MINOR | 1.0.0 → 1.1.0 |
| 不兼容变更（部署结构、配置格式、回退方式） | MAJOR | 1.0.0 → 2.0.0 |

判定标准：**目标机上的使用方式有没有变**。
装完还是双击快捷方式、还是那几个端口、`deploy.json` 还能直接用 → 不算 MAJOR。

---


## [1.0.0] - 2026-11-13

首个完整版本。把原本散落在本机的索引库、运行看板、内存降温、脚本库整合成一键部署包。

### 部署

- **安装器**（`installer.exe`，WinForms）：功能说明 → 环境检测 → 用户确认 → 安装 → 自检 → 完成，
  自检全过才显示「部署完成」
- **环境检测**（`check-env.ps1`）：PowerShell 版本、Node、目录可写、磁盘空间、端口占用、已有部署、规则目录
- **版本检测**（`check-install.ps1`）：读注册表与 `deploy.json`，判定首次安装 / 同版本重装 / 升级 / 降级，
  退出码 `0=未装 1=同版本 2=可升级 3=降级`；安装前界面显示已装版本与部署日期
- **环境监测**（`health-check.ps1`）：27 项只读检查（文件完整性 + 接口可用性 + 运行环境），
  支持 `-Repair` 尝试重启挂掉的服务
- **一键回退**（`uninstall.ps1`）：停服务、删快捷方式、清注册表、移除规则受管区块、删目录
- **多平台规则分发**（`install-rules.ps1`）：同一份规则正文写入 CodeBuddy / Cursor / Windsurf（独占文件）
  与 Claude Code / Codex / Cline（受管区块），默认只为本机已装平台写
- **便携运行时**：随包 Node 24 + MinGit，目标机无需预装任何东西
- **不做开机自启**：不写注册表 Run、不放启动文件夹

### 窗口

- **原生窗口**（`CodeBuddyWorkKit.Native.exe`，默认入口）：纯 WinForms，不依赖任何浏览器
  - 五个视图：概览 / 项目索引 / 脚本库 / 仓库与备份 / 运行看板
  - 顶部报错按钮：平时灰色，有错误变红并显示数量徽章，点开可看时间、来源、详情
  - 设置面板：三个端口 + 监听地址，保存后自动重启服务；内置「可外接内容」清单与环境检测入口
  - 运行时自检：服务健康每 4 秒探测，连续 3 次失败记为错误；主线程阻塞 >20 秒记一条告警
  - 看板趋势图：CPU / 内存最近 40 次采样的迷你折线
- **浏览器嵌入版**（`CodeBuddyWorkKit.Shell.exe`）：Edge `--app` 窗口嵌入窗体，无地址栏；
  最小化与关闭都收进托盘，二次启动唤起已有窗口
- **托盘告警**：Pulse 的重复进程告警推到托盘（图标变色 + 右键菜单最近 5 条）；
  系统通知由页面侧 `Notification` API 发出

### 应用

**CodeVault（索引库）** —— 本机项目索引：构建逻辑、分区职责、已知问题，网页浏览
**CodeBuddy Pulse（运行看板）**
- 工具调用与任务进度采集（hook / 包装命令 / 手动上报三种来源）
- **重复测试进程守护**：同项目同脚本 ≥4 个且连续两轮采样 → 告警；再增加或达 6 个 → 锁死并清理多余进程
  （清理前核对命令行，防 PID 复用误杀）
- 用量统计：事件数 / 字符数 / 估算 token（`estTokens`，按 3.8 字符/token 折算）

**CodeBuddy Idle Tuner（降温）**
- 工作集回收（`EmptyWorkingSet`）
- **系统缓存清理**（可选，需管理员）：待机页表 / 低优先待机 / 已修改页表
  实测待机页可从 428 MB 降到 41 MB

**ScriptVault（脚本库）**
- 8 个工具：`snippet`（检索+执行）、`shot`（截图+CDP）、`git-report`、`adopt-project`、
  `scaffold`、`measure-cost`、`measure-library`、`prune-artifacts`
- 3 个 Python 模块：`excel_report`（报表排版）、`dxf_draw`（图元+图框+标注）、`selftest`
- 分类索引（模块 / 工具 / Python / 测试），支持 `snippet search` 关键词检索

### 文档

- 每个工程根目录一份 `AGENTS.md`：目录地图 + 只记几条命令 + 想改什么改哪里 + 踩过的坑
- 目标机规则（`deploy-target.mdc`）含目录地图与「改什么改哪个文件」对照表
- `docs/使用说明.txt`：安装、使用、升级、回退、多平台适配

---

## 之后怎么加条目

```powershell
# 1.0.1 修复版
powershell -File build\bump-version.ps1 -Kind patch -Note "修复 XX 在 YY 情况下失效"

# 1.1.0 新功能
powershell -File build\bump-version.ps1 -Kind minor -Note "新增 ZZ 能力"

# 2.0.0 不兼容
powershell -File build\bump-version.ps1 -Kind major -Note "部署结构改为 ..."
```

脚本会同时改 `payload/VERSION` 和本文件（插入新条目），然后重新打包即可。
