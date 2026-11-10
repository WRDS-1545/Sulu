# 素履（Sulu）

> CLI 命令：`sulu`
>
> 出处：《易经·履卦》：“素履往，无咎。” 意为以朴素纯洁的态度行事。  
> 意境：清理系统冗余，让操作系统保持“素净”；迁移与备份，则是为了“远行”。

`Sulu` 是一个面向 Windows 10/11 的命令行软件管理工具：

- 列出已安装软件，并为每个卸载注册表项分配稳定的短索引；
- 调用厂商卸载程序，卸载成功后扫描并清理可证明属于该软件的注册表残留；
- 强制删除指定软件的注册表条目（不删除文件），适合卸载器损坏或卸载项残留；
- 在充分备份和审查的前提下，迁移应用目录并重写注册表中的路径引用；迁移和强制删除都保留原目录，并输出手动删除命令；
- 所有 CLI 提示、日志和错误信息使用英文，避免旧终端代码页导致乱码；本文档使用中文。

## 快速开始

```powershell
cd E:\COde\Sulu

.\dist\win-x64\sulu.exe list
.\dist\win-x64\sulu.exe list --verbose
.\dist\win-x64\sulu.exe uninstall 12 --dry-run
.\dist\win-x64\sulu.exe purge 12 --dry-run
.\dist\win-x64\sulu.exe migrate 12 --to "D:\Apps\Example" --dry-run
```

如果希望直接使用 `sulu` 命令，可将发布目录加入 `PATH`，或把 `sulu.exe` 复制到已有的用户工具目录。项目已验证当前机器 `PATH` 中不存在 `sulu`、`sulu.exe`、`sulu.cmd` 或 `sulu.bat` 冲突。

## 功能总览

| 命令 | 用途 |
| --- | --- |
| `sulu list` | 列出软件；显示稳定索引、名称、版本、发布者、来源和大小 |
| `sulu uninstall` | 调用卸载器，成功后扫描、备份并清理注册表残留 |
| `sulu scan` | 只读扫描卸载项、App Paths、Run/RunOnce 足迹 |
| `sulu purge` | 强制删除注册表条目，不碰文件；成功后输出手动删除目录命令 |
| `sulu migrate` | 复制应用文件、重写注册表路径；旧目录始终保留，支持回滚 |
| `sulu help` | 查看总体或单个命令帮助 |

## 稳定索引与完整名称

Windows 中有些软件显示名非常长，例如：

```text
Windows Software Development Kit - Windows 10.0.26100.8249
```

终端宽度有限时，表格无法一次显示完整名称。Sulu 为每个卸载注册表项分配一个持久编号：

```text
+-------+---------------------------------------+-----------------+-----------------------+--------+----------+
| Index | Name                                  | Version         | Publisher             | Source | Size     |
+-------+---------------------------------------+-----------------+-----------------------+--------+----------+
|   487 | Windows Software Development Kit -    | 10.1.26100.8249 | Microsoft Corporation | HKLM32 |   2.3 GB |
|       | Windows 10.0.26100.8249               |                 |                       |        |          |
+-------+---------------------------------------+-----------------+-----------------------+--------+----------+
```

可以直接使用编号：

```powershell
sulu uninstall 487 --dry-run
sulu uninstall 487 --quiet --yes
sulu scan 487 --json
sulu migrate 487 --to "D:\SDK\10.0.26100.8249" --dry-run
```

编号规则：

- 索引文件：`%LOCALAPPDATA%\sulu\index.json`；
- 包括隐藏系统组件和更新项在内，每个注册项都会获得编号；因此默认列表里可能出现编号跳跃；
- 以卸载注册表键的完整路径为身份；同一路径的项沿用同一编号；
- 新增软件获得下一个未使用编号，已有软件不会因为排序变化而改号；
- 已消失软件的历史记录不会被回收；如果以后在同一注册表路径重新安装，会沿用旧编号；
- 如果索引文件被删除或损坏，会在下一次运行时重建；此时编号可能改变；
- `--verbose` 不再输出截断的表格，而是每个软件一个完整信息块，包含完整名称、安装目录和注册表 ID；
- `--json` 包含 `index`、`refName`、`id` 和全部原始字段。

`--filter` 也接受索引，例如 `sulu list --filter 487`。

在 PowerShell 中，`#487` 会被当作注释起点。请使用 `487` 或 `--index 487`，不要直接输入未加引号的 `#487`。

## 卸载与清理

```powershell
# 交互确认；支持 MSI 和普通 EXE 卸载器
sulu uninstall "Example App"

# 使用索引，非交互静默卸载并自动确认清理
sulu uninstall 12 --quiet --yes

# 只看将执行什么，不改注册表
sulu uninstall 12 --dry-run

# 只卸载，不清理
sulu uninstall 12 --no-clean --yes

# 精确指定注册表 ID
sulu uninstall --id "HKLM64:SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{GUID}" --yes
```

常用参数：

| 参数 | 说明 |
| --- | --- |
| `--index <n>` | 精确指定稳定索引 |
| `--id <id>` | 精确指定注册表 ID，与 `--index` 互斥 |
| `--yes`, `-y` | 跳过卸载和清理确认 |
| `--quiet`, `-q` | 使用 `QuietUninstallString`；MSI 使用 `/qn` |
| `--dry-run` | 只展示命令和扫描预览，不执行卸载、不写备份 |
| `--no-clean` | 跳过后置注册表清理 |
| `--pause` | 卸载器退出后、注册表扫描前等待用户按回车 |
| `--no-pause` | 不等待回车；`--yes` 模式下默认如此 |
| `--timeout <秒>` | 等待卸载器退出，默认 1800，0 表示无限等待 |
| `--settle <秒>` | 等待卸载项消失，默认 5 |
| `--backup-dir <目录>` | `.reg` 备份父目录 |

卸载器退出码 `0`、`3010`、`1641` 视为成功；`1602` 视为用户取消；其它非零退出码不会触发清理。

有些厂商卸载器会启动后台 helper 后立刻返回，因此“退出码为 0”不代表它真的完成了。交互式运行 `sulu uninstall` 时，工具会在卸载器退出后、注册表扫描前暂停：

```text
The uninstaller process has exited, but some installers start a helper and return early.
Wait until the vendor uninstaller has fully finished, then press Enter to scan registry leftovers.
Press Enter to continue...
```

- 默认只在交互式终端、且未使用 `--yes` 时等待；
- `--pause` 可强制等待，即使同时使用了 `--yes`；
- `--no-pause` 可关闭等待；
- `--pause` 需要交互式 stdin，重定向输入时会直接拒绝，避免破坏自动化脚本；
- 按回车后仍会执行 `--settle` 宽限期，再开始扫描。

### 清理范围

只有同时满足“卸载成功”与“能证明归属”的项才会自动删除：

| 位置 | 自动清理条件 |
| --- | --- |
| `...\CurrentVersion\Uninstall` | 与原软件同 MSI ProductCode，或同显示名且同安装目录的遗留项 |
| `...\CurrentVersion\App Paths` | 默认值指向原软件 `InstallLocation` 之下 |
| `...\CurrentVersion\Run` / `RunOnce` | 命令引用了原软件 `InstallLocation` 之下的完整路径 |

不会自动删除：

- 原软件仍存在的卸载注册表项；
- 同名但发布者、产品代码或安装目录不同的卸载项；
- `HKLM\SOFTWARE\Vendor\Product`、服务、驱动、文件关联、COM 注册等无法可靠归因的键；
- 安装目录中的任何文件（卸载命令不会清理文件）。

卸载项仍存在、卸载失败、超时或取消时，所有现存设置都会被保护并标记为 `REVIEW`。

删除前强制使用 `reg.exe export` 备份每个涉及的注册表键；备份失败则清理中止。

默认备份目录：

```text
%LOCALAPPDATA%\sulu\backups\<时间戳>-<软件名>\
```

恢复示例：

```powershell
reg import "$env:LOCALAPPDATA\sulu\backups\20261110-120000-Example-App\01-App-Path.reg"
```

HKLM 的备份需要从管理员终端导入。

## 强制删除注册表条目

`purge`（别名 `force-delete`）用于卸载器已损坏、卸载项残留，或明确要强制移除注册表条目的场景。它**只删除注册表条目**，不调用卸载器，也不删除任何文件。

```powershell
# 只查看将删除哪些注册表项
sulu purge 12 --dry-run

# 交互确认后强制删除
sulu purge 12

# 非交互
sulu purge 12 --yes
```

删除范围严格限定为：

- 该软件自己的 Uninstall 注册表键；
- 指向其 `InstallLocation` 的 App Paths 项；
- 指向其 `InstallLocation` 的 Run / RunOnce 值。

同名的其它版本、发布者、ProductCode 的卸载项不会被顺带删除。删除前强制创建 `.reg` 备份。成功后工具不会删除安装目录，而是打印一条需要用户自行执行的命令：

```powershell
Remove-Item -LiteralPath 'D:\Apps\Example' -Recurse -Force
```

如果 `InstallLocation` 缺失、是共享根目录，或目录不存在，则不会生成删除命令，只显示说明。先确认文件确实可以删除，再执行该命令；该命令会永久删除目录及其中所有内容。

## 应用路径迁移

`migrate` 合并了原 `AppPathMove-Registry.ps1` 的扫描、审核和回滚思路，并增加了文件复制：

```powershell
# 只预览；不改文件、不改注册表
sulu migrate 12 --to "D:\Apps\Example" --dry-run

# 交互确认；复制文件、改写安全引用，旧目录始终保留
sulu migrate 12 --to "D:\Apps\Example"

# 自动确认
sulu migrate 12 --to "D:\Apps\Example" --yes

# 已经手工移动过文件，只改注册表
sulu migrate 12 --to "D:\Apps\Example" --registry-only --yes

# 连 REVIEW 项一起修改（必须先检查输出或 CSV）
sulu migrate 12 --to "D:\Apps\Example" --include-review
```

`--keep-source` 仍可传入以兼容旧脚本，但现在只是默认行为的显式写法；迁移不再自动删除旧目录。

### 迁移流程

1. 解析软件索引、注册表 ID 或名称；
2. 读取 `InstallLocation`，或用 `--from` 指定旧目录；
3. 检查旧目录、新目录、路径包含关系、驱动器根目录和共享系统目录；
4. 检查旧目录中是否有正在运行的进程；
5. 扫描 `HKLM`、`HKCU`、`HKU`、`HKCC` 的 64 位和 32 位视图；
6. 只处理 `REG_SZ` / `REG_EXPAND_SZ` 中“完整旧目录路径”的引用，避免 `D:\App` 误匹配 `D:\App2`；
7. 创建迁移记录和 `references.csv`；
8. 使用 `robocopy` 复制文件；
9. 逐项重新读取并校验当前值，确认未被改动后写入新值；
10. 旧目录始终保留，成功后在输出中打印一条手动删除命令；
11. 失败时自动回滚已改写的注册表值；源目录始终保留。

迁移成功后会看到类似：

```text
Manual directory cleanup
The old application directory is intentionally kept for rollback. After verifying that the application starts and works from the new location, delete the old directory manually:
  Remove-Item -LiteralPath 'D:\Apps\OldExample' -Recurse -Force
```

### 为什么保留旧目录

- 回滚时如果旧目录仍在，只需恢复注册表值，不需要把文件从新目录再复制回来；
- 避免反复复制大量文件；
- 在确认新位置可用之前，不会破坏原安装；
- 删除动作由用户在看到明确路径后自行执行，减少误删风险。

### 安全引用与 REVIEW 引用

自动修改（`SAFE`）：

- 该软件自己的 Uninstall 注册表项中的字符串值；
- 指向旧目录的 App Paths 项；
- 指向旧目录的 Run / RunOnce 命令。

需要人工确认（`REVIEW`）：

- `Software\Vendor\Product` 等应用设置；
- 文件关联、COM、CLSID 等其它注册表引用；
- 其它用户配置（HKU）中的引用；
- 其它引用旧目录的卸载项。

服务/驱动引用单独标记为 `SERVICE`。发现服务引用时，默认拒绝移动文件，避免把正在运行的服务路径改坏。只有同时使用 `--include-review --force` 才会继续，并且必须确认服务引用也会被更新；`--force` 不会自动停止、重启或重配服务。

默认只改 `SAFE` 项。`--include-review` 会把 `REVIEW` 项也加入修改；服务/驱动项还需要 `--force`。迁移记录目录中的 `references.csv` 列出全部引用、当前值、建议值和是否被选中，便于事后审查。

### 不处理的内容

- **MSI 产品默认拒绝迁移**。MSI 的内部组件数据库仍记录旧路径，简单搬目录会破坏修复、升级和卸载。请卸载后在目标位置重新安装。
- 快捷方式、开始菜单项、桌面快捷方式；
- 计划任务；
- 服务控制管理器配置、驱动加载状态、已运行服务；
- 应用自己的配置文件、缓存和数据库；
- 文件关联和 COM 注册中未包含完整旧路径的值；
- Windows Installer 缓存。

迁移后如有关联功能失效，请先检查 `references.csv` 和迁移记录，再决定是否补充处理。

### 回滚

迁移记录默认位于：

```text
%LOCALAPPDATA%\sulu\migrations\<时间戳>-<软件名>\
├── migration.json
└── references.csv
```

回滚：

```powershell
sulu migrate --restore "$env:LOCALAPPDATA\sulu\migrations\20261110-120000-Example\migration.json"
```

回滚行为：

- 默认迁移会保留旧目录，因此回滚通常只恢复注册表值，不再复制文件；
- 如果用户已经手动删除旧目录，而新目录仍存在，回滚会先把新目录复制回旧路径；
- 当前值与迁移后值不一致时默认跳过，防止覆盖迁移后的手工改动；`--force` 可强制覆盖；
- 如果回滚时复制了新目录，成功后删除该新目录；`--keep-source` 可保留；
- 如果旧目录仍在，注册表恢复后新目录成为冗余副本，工具会打印手动删除新目录的命令；
- `--registry-only` 只恢复注册表，不复制或删除文件；
- `--dry-run` 只展示计划。

## 构建与发布

要求：

- Windows 10/11
- .NET 10 SDK 或更高版本（目标框架 `net10.0-windows`）

```powershell
dotnet build Sulu.sln -c Release
dotnet run --project src\Sulu\Sulu.csproj -- list
```

发布单文件版本：

```powershell
# 依赖 .NET 10 Runtime，体积小
.\publish.ps1 -Runtime win-x64

# 自包含，目标机无需安装 .NET
.\publish.ps1 -Runtime win-x64 -SelfContained
```

输出位于：

```text
dist\<runtime>\sulu.exe
```

## 退出码

| 代码 | 含义 |
| --- | --- |
| `0` | 成功 |
| `1` | 未预期错误 |
| `2` | 参数错误 |
| `3` | 未找到软件、索引或名称有歧义 |
| `4` | 用户取消 |
| `5` | 卸载器启动失败、超时或返回失败 |
| `6` | 需要管理员权限 |
| `7` | 清理或备份失败 |
| `8` | 路径迁移失败、文件复制失败或迁移记录无法使用 |

## 测试

```powershell
pwsh -NoProfile -File tests\integration.ps1 -Configuration Release
```

测试会：

- 验证版本、帮助、索引唯一性和索引文件持久化；
- 验证索引方式的 `uninstall --dry-run` 和 `scan`；
- 在可写 HKCU 的普通 Windows 会话中，创建随机命名的测试项，验证：
  - 超长名称在 `--verbose` 中完整显示；
  - 卸载、备份、清理和无关项保护；
  - 文件迁移、注册表改写、旧目录保留和手动删除命令；
  - `--restore` 注册表回滚，以及旧目录缺失时的文件回滚；
  - `purge` 强制删除注册表条目且保留文件；
- 在低完整性/沙箱会话中只运行只读检查，并明确提示跳过破坏性测试；
- 所有测试注册表项和临时目录都在 `finally` 中清理。

## 已知限制

- 只支持注册表 `Uninstall` 中的经典 Win32/MSI 软件，不支持 Microsoft Store/UWP/AppX。
- 静默参数完全依赖厂商提供的 `QuietUninstallString` 或 MSI；本工具不猜测 `/S`、`/silent` 等参数。
- 如果软件没有记录 `InstallLocation`，卸载清理只能检查卸载项；迁移需要显式 `--from`。
- 安装目录是 `C:\Program Files`、用户目录等共享根目录时，拒绝按目录做 App Paths/Run 匹配，避免误删同目录其它软件。
- 路径迁移只更新注册表中的字符串值，不更新快捷方式、计划任务、服务和配置文件；详见上文。
- 如果其它已注册软件记录同一个 `InstallLocation`，迁移默认拒绝，必须用 `--force` 明确承担共享目录迁移的风险。
- 迁移只匹配注册表中直接写出的旧目录文本。若某个值使用 `%ProgramFiles%` 等环境变量、正斜杠或其它等价写法，而 `InstallLocation` 是展开后的路径，则该值不会被自动匹配，需要人工检查。
- 迁移扫描会遍历较大范围的注册表。普通桌面环境通常需要几秒；不可访问的系统键会被跳过并计数。
- 迁移不是事务。工具通过“先复制、再改注册表、保留旧目录”和迁移记录降低风险，但极端情况下（断电、磁盘满、权限变化）仍可能需要手工恢复。
- `purge` 强制删除后，软件的文件、服务、计划任务和设置不会被清理；安装目录的删除命令需要用户自行确认和执行。
- 卸载器需要 UAC 时，请从管理员终端运行；否则卸载器可能启动失败。
