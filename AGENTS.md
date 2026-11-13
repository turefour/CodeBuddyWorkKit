# CodeBuddy 工作套件 · 部署包工程

把一个本地工作环境（索引库 / 运行看板 / 内存降温 / 脚本库）打成 Windows 一键安装包。
目标机只需要解压 + 双击 `installer.exe`，不需要预装 Node、Git、浏览器。

---

## 目录地图

```
build/build-package.ps1     唯一的打包入口，跑它就会出 zip
installer/Installer.cs      安装器（WinForms）：说明 → 检测 → 确认 → 安装 → 自检
payload/                    被打进 zip 的东西
  apps/                     四个应用（由下面「源码真相源」复制而来）
  tools/                    部署与运维脚本（见下表）
  native/                   原生窗口（纯 WinForms，不依赖浏览器）
  shell/                    浏览器嵌入版窗口（Edge --app + SetParent）
  rules/deploy-target.mdc   目标机规则模板
  config/deploy.template.json
  runtime/                  便携 node.exe + MinGit
  docs/使用说明.txt
installer.exe               打包产物，不入库
```

## 只记四条命令

```powershell
# 1. 出包（会重新复制 apps、清私有数据、编译两个 exe、打 zip）
powershell -ExecutionPolicy Bypass -File build\build-package.ps1

# 2. 单独编译原生窗口（改完 payload/native/*.cs 用）
$csc='C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
& $csc /nologo /target:winexe /out:payload\native\CodeBuddyWorkKit.Native.exe `
  /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Web.Extensions.dll `
  payload\native\Theme.cs payload\native\Api.cs payload\native\NativeForm.cs payload\native\Program.cs

# 3. 部署到测试目录（跳过安装器，直接验证部署逻辑）
powershell -File payload\tools\deploy.ps1 -Target C:\temp\kit -Port 49610 -PulsePort 49621 -TunerPort 49622 -NoDesktop -NoRules

# 4. 部署后自检（18 项：文件层 + 进程层 + 接口层）
powershell -File <目标目录>\tools\test-deploy.ps1 -Target <目标目录>
```

## 想改什么 → 改哪个文件

| 需求 | 文件 |
|---|---|
| 安装界面文案 / 流程 | `installer/Installer.cs` |
| 原生窗口界面 | `payload/native/NativeForm.cs`（布局）、`Theme.cs`（配色/字体/画刷） |
| 部署逻辑（复制、写配置、快捷方式、规则分发） | `payload/tools/deploy.ps1` |
| 安装前环境检测 | `payload/tools/check-env.ps1` |
| 已安装版本检测 | `payload/tools/check-install.ps1` |
| 部署后环境监测 | `payload/tools/health-check.ps1` |
| 一键回退 | `payload/tools/uninstall.ps1` |
| 多平台规则分发 | `payload/tools/install-rules.ps1` |
| 目标机上的 AI 规则 | `payload/rules/deploy-target.mdc` |
| 启动 / 停止服务 | `payload/tools/launch.mjs` / `stop.mjs` |

## 源码真相源（重要）

`payload/apps/` 是**复制品**，不要直接改它 —— 下次打包会被覆盖。

| 包内路径 | 真实源码 |
|---|---|
| `payload/apps/codevault` | `C:\Users\GH\Desktop\CodeVault` |
| `payload/apps/pulse` | `C:\Users\GH\CodeBuddy\20261029081733\codebuddy-pulse` |
| `payload/apps/tuner` | `C:\Users\GH\CodeBuddy\20261029081733\codebuddy-idle-tuner` |
| `payload/apps/scriptvault` | `C:\Users\GH\Desktop\ScriptVault` |

改应用 → 改源码目录 → 跑 build-package。

## 陷阱（全部实际踩过）

1. **PS1 必须带 UTF-8 BOM**。PS 5.1 按 GBK 解析无 BOM 的 UTF-8 文件，中文会变乱码并报语法错。
   ```powershell
   $bom = New-Object System.Text.UTF8Encoding $true
   [System.IO.File]::WriteAllText($p, [System.IO.File]::ReadAllText($p,[Text.Encoding]::UTF8), $bom)
   ```

2. **`config\deploy.json` 必须无 BOM**。Node 的 `JSON.parse` 遇到 BOM 直接抛错，而 `Set-Content -Encoding UTF8` 会写 BOM。
   用 `[System.IO.File]::WriteAllText($p, $json, (New-Object System.Text.UTF8Encoding $false))`。

3. **`payload\shell\state\` 是运行时残留**。跑过 Shell.exe 会留下 80 MB 的 Edge profile，打包前必须删（脚本里已加）。

4. **编译 native exe 前先杀进程**，否则 `Remove-Item` 报 Access denied。

5. **WinForms 的 `Dock` 依赖 z-order**，`Dock=Fill` 的容器会把边栏/状态栏挤掉。
   本工程改用 `LayoutShell()` 绝对定位，不要改回 Dock。

6. **组合框/列表的 OwnerDraw 必须设 `e.DrawDefault = false`**，否则点击时系统会先用默认色画一遍再被覆盖。

7. **字体和画刷要缓存**。`Theme.Body` 是静态字段不是属性 —— 写成 `get { return new Font(...) }` 会在每次绘制时创建 GDI 对象，句柄只增不减。

## 验证标准

改完部署相关代码，至少跑一遍：

```powershell
# 解压 → 静默安装 → 自检
Expand-Archive CodeBuddyWorkKit-1.0.0.zip -DestinationPath $stage
& "$stage\installer.exe" --silent --target $target --port 49610 --pulse 49621 --tuner 49622
powershell -File "$target\tools\test-deploy.ps1" -Target $target   # 要 18 项全过
```

## 版本约定

- 版本号唯一来源：`payload/VERSION`（打包时写入，`deploy.ps1` 读它写进目标机 `deploy.json`）
- 检测已装版本：`tools\check-install.ps1`，退出码 `0=未装 1=同版本 2=可升级 3=降级`
- 安装位置登记在注册表 `HKCU\Software\CodeBuddyWorkKit`
