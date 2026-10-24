# Open With Cleaner Desktop Suoh Edition · macOS

原生 macOS「打开方式」残留检查与定向清理工具，使用苏芳主色 `#992C49`。首版版本为 **0.1.0**，与 Windows 版分别编号。

## 使用

1. 完整解压 macOS 通用 ZIP，将 `.app` 拖到“应用程序”文件夹。
2. 打开应用并点击“扫描记录”。扫描只读取系统信息，不修改文件关联。
3. 搜索应用名称、路径或 Bundle Identifier，核对选中记录的路径与判断原因。
4. 只有“可清理残留”可以点击“预览清理…”。确认后会保存操作记录，再注销该精确路径的旧注册。
5. 重新打开 Finder 的文件菜单检查结果。界面仍未更新时，可自行选择“更多 → 重新启动 Finder…”。

最低系统为 **macOS 13**，同一程序包含 Apple Silicon（arm64）和 Intel（x86_64）架构，不需要 Rosetta、Python 或其他运行时。

此构建使用 ad hoc 签名以检查应用完整性，**没有 Developer ID 签名或 Apple 公证**。从互联网下载后，macOS 可能阻止首次运行。确认下载来源后，可在尝试打开后前往“系统设置 → 隐私与安全性”查看“仍要打开”选项；受组织策略管理的 Mac 可能不允许例外。不要关闭 Gatekeeper 或系统安全功能。

## 识别规则

- 默认隐藏路径确认存在的应用，勾选“显示已安装应用”可查看。
- 必须同时满足：系统标记 `Bundle node not found on disk`、本地路径确认不存在、应用标识与路径无歧义、未发现默认关联保护，才允许清理。
- 外接盘、磁盘映像、网络位置、系统应用、符号链接或权限无法确认的记录保留。首版不清理 `/Volumes` 下的记录，即使磁盘当前已挂载。
- 只有应用包本身缺失、父目录仍可访问且位于已确认的系统本地卷时才允许清理；父目录也消失的更新缓存等位置会保留待确认。
- 清理前重新读取数据库、默认关联与路径，防止扫描后重新安装或关联改变。
- 清理后再次读取，确认所选路径的记录消失；命令退出码为 0 本身不算成功。
- 未识别的系统输出不会转换成清理操作；整份输出无法识别时，扫描显示错误。

默认关联保护仅覆盖系统可读取到的显式选择和已识别类型，不能保证枚举所有动态类型或处理程序。本版因此始终保留实际存在的应用；不会提供“清掉所有已安装应用候选”的操作。

列表反映每次扫描的实际状态，没有永久隐藏名单。仍然安装的应用由系统正常注册，本版不提供永久隐藏。重新安装后可以再次被系统发现，也可以通过“重新注册应用…”选择实际存在的 `.app`。

## 操作记录与隐私

记录位置：

```text
~/Library/Application Support/OpenWithCleanerMac/Operations/
```

每次清理保存操作前后的系统转储、所选项目、命令结果和验证结果。**这些是诊断与审计快照，不是能够完整恢复 Launch Services 的数据库备份。** 已不存在的应用无法仅凭文本快照恢复注册；重新安装后可以重新注册，默认应用仍需自行核对。

程序不会自动上传诊断或操作记录。记录包含本机应用路径和标识符，分享前应检查。正常工作不需要管理员权限，不删除 `.app` 文件，不改写其他应用的 `Info.plist`，不执行全库重置、不自动终止 Finder。

## 实际文件检查

“检查文件…”通过 Apple 的 `NSWorkspace` 查询所选文件的默认应用和打开候选。这是系统 API 返回结果，不能保证与每个 macOS 版本 Finder 菜单的即时显示完全一致。需要修改默认应用时，在 Finder 中选中文件 → 显示简介 → 打开方式 → 全部更改。

## 构建与测试

需要 Mac 和 Xcode Command Line Tools（Swift 5.9 或更高版本）。无第三方包依赖。

```bash
swift test --package-path macos
OWC_RUN_INTEGRATION=1 swift test --package-path macos --filter MacIntegrationTests
bash macos/scripts/build.sh
```

集成测试只注册测试中新建的唯一 `.app`，确认其安装状态后删除该临时应用，再通过真实系统工具注销这个路径，核对记录消失。它不修改已有应用的默认打开方式。请仅在开发或 CI 环境主动运行集成测试。

构建输出在 `macos/dist/`，包含通用 `.app`、ZIP 和 SHA-256 校验文件。

## 系统兼容边界

扫描和定向注销使用 macOS 自带的 `lsregister`。它不是 Apple 承诺稳定的公开解析接口，系统升级可能改变输出或行为。程序采取保守规则：不能确认就保留，不以全库重建作为失败后的自动兜底。系统服务、软件更新和重新安装仍可能产生新注册记录。

参考：[Apple Launch Services](https://developer.apple.com/documentation/coreservices/launch_services)、[Apple 应用注册机制](https://developer.apple.com/library/archive/documentation/Carbon/Conceptual/LaunchServicesConcepts/LSCConcepts/LSCConcepts.html)、[NSWorkspace](https://developer.apple.com/documentation/appkit/nsworkspace)。
