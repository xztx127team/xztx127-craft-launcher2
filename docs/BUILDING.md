# 构建与发布 / Building and publishing

本文描述当前源码中的构建入口，不把未执行的构建写成成功。根目录 README 是项目介绍。

## 中文

### 环境和已知边界

- 使用 Windows 桌面与 .NET 8 SDK。WPF 主工程为 `XCL2.App/XCL2.App.csproj`，目标 `net8.0-windows10.0.19041.0`，最低支持平台声明为 `10.0.17763.0`。最低声明不是所有外部服务、驱动和附加组件的兼容性保证。
- `XCL2.App/WinUI.Preview` 是单独的 WinUI 工程；主工程有联动构建目标。仅编译 WPF 时加 `-p:SkipNativePreview=true`。
- x86、x64、ARM64 是工程构建配置。Terracotta 随附说明指出 v0.4.2 没有 Windows x86 发行包，本源码包也不附其 EXE；不能因此声称三个架构都支持所有功能。
- **当前阻碍无配置公开构建的因素**：项目会校验 `XCL2_PRE_KEY` 与 `XCL2_REL_KEY`，二者各为 96 位十六进制值，属于原有基岩版模块。有效值不在仓库中。缺少时编译会失败；本次文档没有生成密钥、删除校验或修改该模块。

维护者在发布二进制前，应准备并说明合法可用的构建途径，提供 AGPL 要求的对应源码和构建材料。可合法公开的配置与真正需要保密的凭据应分别处理；不能将一个不能构建的源码快照直接宣称为完整履约。参见[分发指南](DISTRIBUTION.md)。

### 编译 WPF

先通过自己的构建环境准备有效配置，不要把真实值提交到 Git。以下检查只验证格式，不输出值，也不证明其授权或有效性：

```powershell
foreach ($name in @('XCL2_PRE_KEY', 'XCL2_REL_KEY')) {
    $value = [Environment]::GetEnvironmentVariable($name)
    if ($value -notmatch '^[0-9A-Fa-f]{96}$') {
        throw "$name is missing or has the wrong format."
    }
}
```

从仓库根目录执行：

```powershell
dotnet restore .\XCL2.App\XCL2.App.csproj
dotnet build .\XCL2.App\XCL2.App.csproj -c Release -p:SkipNativePreview=true
```

如果要联动构建独立 WinUI 程序，移除 `SkipNativePreview`，并按该子工程准备 Windows App SDK 相关构建环境。WPF 内的新界面预览与这个独立程序不是同一个构建产物。

### 发布示例

```powershell
dotnet publish .\XCL2.App\XCL2.App.csproj -c Release -r win-x64 --self-contained true -p:SkipNativePreview=true -o .\artifacts\win-x64
```

此命令是构建方法，不表示交付时已经执行成功。按目标架构调整 RID；验证所有本机依赖与游戏功能。保留完整发布目录，尤其是 WebView2 托管组件、本机 Loader 和需要的 `runtimes` 目录。单文件选项也不代表可以只发一个 EXE。

准备发行包时，附基础许可、来源声明、适用的依赖许可，以及与二进制一致的源码入口。第三方包按[分发指南](DISTRIBUTION.md)填写身份说明；本指南不会自动为任意构建添加这些文件。

### 回归检查

```powershell
powershell -ExecutionPolicy Bypass -File .\XCL2.App\Tests\Test-AiPresentation.ps1
powershell -ExecutionPolicy Bypass -File .\XCL2.App\Tests\Test-October29Changes.ps1 -IncludeWindowsUi
```

测试需要 Windows；部分会短暂创建窗口或测试进程。添加 `-BuildLauncher` 可让对应脚本同时构建主程序。其他回归工程位于 `XCL2.App/Tests`，按修改范围选择。

此前源码交付检查包含语法树、XAML/XML、引用与压缩包完整性。交付环境的 CoreCLR 初始化失败，且没有 Windows 桌面，因此没有 Windows 编译、UI 自动化或实机验证结果。本次仅新增文档、许可文件与 README 图标，没有以静态检查替代运行测试。

## English

Use a Windows desktop and the .NET 8 SDK. The main WPF project targets `net8.0-windows10.0.19041.0` and declares minimum platform `10.0.17763.0`. Components and services may impose additional requirements. Build configurations for x86, x64, and ARM64 do not imply universal dependency support; the bundled Terracotta notice records no Windows x86 release for v0.4.2, and this source package contains no Terracotta executable.

**Known public-build limitation:** the existing Bedrock module validates `XCL2_PRE_KEY` and `XCL2_REL_KEY`, each as a 96-character hexadecimal value. Values are not included. Missing configuration causes the build to fail. This documentation update neither supplies keys nor removes validation. Obtain a lawful configuration through your own build setup; do not commit actual credentials. The PowerShell check above validates format without printing values.

From the repository root, run the restore and build commands above. `SkipNativePreview=true` skips the separate WinUI application, not the WPF interface-preview styles. To build that companion application, remove the property and satisfy its Windows App SDK build requirements.

The publish example produces a self-contained `win-x64` output when its prerequisites are met; change the RID for other architectures and verify native dependencies. Distribute the complete output, including required WebView2 components and loaders. A publish command in documentation is not a successful build report.

Run the regression scripts on Windows; some open temporary windows or processes. `-BuildLauncher` also builds the main project. Other focused checks are in `XCL2.App/Tests`.

Before publishing binaries, provide a lawful, documented build path and all materials required as Corresponding Source. Resolve external configuration requirements rather than claiming a non-buildable snapshot necessarily satisfies AGPL. Include license and origin documents and applicable dependency notices. Follow the [distribution guide](DISTRIBUTION.md) for unofficial packages; this guide does not automatically copy legal documents into build output.

Previous delivery validation was static. CoreCLR could not initialize in the delivery environment, and no Windows desktop was available. No successful Windows build or runtime UI validation is claimed. This update adds documentation, licensing files, and the README image only.
