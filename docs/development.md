# 开发与验证

需要Windows x64、Git和.NET SDK 10.0.403（global.json允许同功能带补丁升级）。NativeAOT发布还需要Visual Studio C++桌面工具与Windows SDK；Inno Setup 7用于安装包。工具链不放入仓库。

在项目根目录使用PowerShell：

```powershell
dotnet restore NufeCampus.slnx
dotnet build NufeCampus.slnx -c Release
dotnet format whitespace NufeCampus.slnx --verify-no-changes
./build.ps1
./installer/build-installer.ps1 -Compiler 'PATH/TO/ISCC.exe'
```

SDK不在PATH时向脚本传`-Dotnet 'PATH/TO/dotnet.exe'`。免安装输出为artifacts/preview-win-x64，安装包为dist；不要覆盖正在运行的发布目录。前台需使用普通Windows用户，勿提权运行。

## 选择检查

| 改动 | 对应检查项目/参数 |
|---|---|
| 保活策略与错误处理 | PolicyChecks |
| 表单、HTTP、平台写入与导出 | SelfChecks、PlatformChecks |
| 多账号、DPAPI、白名单与兼容 | FeatureChecks |
| 当前接口与网速 | NetworkChecks |
| 前台只读契约 | KeepAliveChecks --foreground-only |
| 前台间隔存储/备份/账号切换 | KeepAliveChecks --observation-settings-only |
| 计划名称存储、迁移、冲突回退及安装维护 | KeepAliveChecks --task-name-only src/NufeCampus.SetupSupport/bin/Release/net10.0-windows/NufeCampus.SetupSupport.dll（先构建解决方案；仅操作禁用触发器的隔离任务） |
| Agent/IPC/任务真实隔离边界 | WindowsChecks --package artifacts/preview-win-x64 |
| CLI及两种策略安装维护 | KeepAliveChecks --package artifacts/preview-win-x64 |
| 安装/升级/卸载 | PackageChecks dist/NUFE-Campus-Setup-1.0.0-x64.exe PATH/TO/EVIDENCE |
| 个人官方XLS交叉核对 | HistoryExportChecks --verify-file PATH/TO/OFFICIAL.xls；人工选择，不进入CI |

例如`dotnet run --project checks/PolicyChecks -c Release`。`tools/Test.ps1`运行较完整检查并可能配置隔离任务；日常按改动选相关专项，避免重复整套测试。CI只构建并执行离线/合成检查，不发布、不安装、不使用真实校园账号，也不视为真实校园网验收。

## 隔离与证据

--test-root只接受TEMP直属NufeCampus-test-<32位小写GUID>。Agent强制离校，自助用合成响应；安装测试另限定TESTROOT/app和独立任务名。不要用真实安装验证卸载，不真实断网、休眠或操作校园业务。

WPF界面以原生窗口验证；将合成截图和日志放在docs/campus-validation-evidence（忽略目录）。构建成功不等于真实校园网、长期资源、其他Windows版本或其他设备已通过。不得把合成样例宣称为真实业务验收。
