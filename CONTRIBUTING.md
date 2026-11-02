# 参与开发

欢迎报告问题和提交改进。先阅读[开发说明](docs/development.md)和[架构](docs/architecture.md)。

提交问题时提供程序版本、Windows版本、复现步骤及预期/实际行为。不要附账号、密码、校园网IP/MAC、Cookie、原始平台响应、个人账单或未脱敏诊断。安全问题参照[安全说明](SECURITY.md)。

修改前区分认证状态、外网状态和保活状态。只读查询不能顺带认证；业务写入不能因超时自动重发。跨进程、任务调度和卸载逻辑应保留当前用户作用域与隔离测试能力。

提交前运行对应检查和`dotnet build NufeCampus.slnx -c Release`；代码格式使用`dotnet format whitespace NufeCampus.slnx`。PR说明改了什么、为何修改、验证方式及未覆盖范围。小改动无需重复完整测试或长期资源采样。

新增公开文件需更新源码导出白名单，并运行`./tools/Test-PublicSource.ps1`。第三方代码需保留来源和许可声明。对本项目的贡献按仓库MIT许可证提供。

