# 第三方组件

本项目代码及自绘SVG/图标按[MIT](LICENSE)发布。项目名称和界面不代表校方授权或官方身份。

| 组件 | 用途 | 当前版本 | 许可及声明 |
|---|---|---|---|
| [AngleSharp](https://github.com/AngleSharp/AngleSharp) | 自助平台HTML解析 | 1.8.6 | MIT，[原许可](licenses/AngleSharp.txt) |
| [ExcelDataReader](https://github.com/ExcelDataReader/ExcelDataReader) | 官方XLS记录读取 | 3.9.0 | MIT，[原许可](licenses/ExcelDataReader.txt) |
| [.NET runtime](https://github.com/dotnet/runtime) | 自包含运行时 | 10.0.14（当前发布） | [许可](licenses/DotNet-LICENSE.TXT)、[第三方声明](licenses/DotNet-THIRD-PARTY-NOTICES.TXT) |
| [WPF/Windows Desktop](https://github.com/dotnet/wpf) | 原生Windows界面 | 10.0.14（当前发布） | [许可](licenses/DotNet-WindowsDesktop-LICENSE.txt) |

依赖版本以csproj和发布的runtimeconfig.json为准。AngleSharp和ExcelDataReader许可从NuGet元数据所记录的固定源码提交获取，保留原版权声明；没有将第三方许可证改为本项目的版权声明。

Inno Setup是外部安装包编译工具，不包含在源码或发布目录中；使用者需遵守所用编译器版本的许可。Windows、系统字体和系统API不随源码提供。发布时应复核依赖变动，并保留licenses目录。
