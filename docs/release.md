# GitHub与发布

源码采用MIT许可证，版权标注为NUFE Campus contributors。源码仓库只收代码、图标、公开文档及构建/检查定义；安装包放Release附件，不提交artifacts/dist、账号数据或验收截图。

## 发布前

1. 检查本次差异、第三方声明与许可证，确认没有个人账号、密码、Cookie、MAC、原始响应或个人表格。
2. 运行相关专项检查和解决方案构建；按需执行原生界面验收。CI工作流需推送后才能在GitHub运行，本地通过不代表GitHub已通过。
3. 运行`./tools/Test-PublicSource.ps1`，验证Git忽略规则与公开文件白名单一致。
4. 运行`./tools/Pack-Source.ps1`，再运行`./tools/Test-PublicSource.ps1 -SourceArchive dist/NUFE-Campus-Source-1.0.0.zip`；检查归档条目及SHA256。
5. 从审查后的目录初始化/提交仓库，再检查待提交文件。已有Git历史须另外审查，.gitignore不会擦除已提交秘密。自动检查覆盖有限高置信模式，不能代替人工审查。

导出白名单位于tools/PublicSource.ps1，新增公开文件时须同步。docs/local-history和docs/campus-validation-evidence只保留在本地，源码ZIP与Git均排除；不应把整个桌面目录作为附件上传。也可以将已检查源码ZIP解压到空目录，作为首次建仓内容。

## 二进制

版本统一在Directory.Build.props设置，安装脚本从该文件读取，不手改多份版本。运行build.ps1后使用installer/build-installer.ps1制作当前用户安装程序；安装EXE及SHA256由脚本输出到dist。发布目录需包含LICENSE、THIRD-PARTY-NOTICES.md及licenses；依赖/运行时变更时重新核对许可声明。

安装和升级不执行保活任务；新安装不自动创建任务。发布前维护者仍应手动确认目标Windows版本、DPI与真实校园环境。当前签名和自动更新未配置，不宣称安装包已签名或跨设备兼容已完成。

当前整理不创建GitHub仓库、不推送、不自动发布Release。首次上传时需由维护者决定账号、仓库名与发布内容。
