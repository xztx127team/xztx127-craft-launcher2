<p align="center"><img src="../assets/xcl2-mark.png" alt="XCL2 project mark: a blue and cyan X with a launch arrow" width="128" height="128"></p>
<h1 align="center">XCL2</h1>
<p align="center"><strong>A clearer desktop for launching games, managing instances, and working with AI.</strong></p>
<p align="center">Windows · .NET 8 · WPF · AGPL-3.0</p>
<p align="center"><a href="../../README.md">简体中文</a> · <a href="README.zh-Hant.md">繁體中文</a> · <strong>English</strong> · <a href="README.ru.md">Русский</a> · <a href="README.ja.md">日本語</a></p>

---

XCL2 is an independent Windows game launcher centered on Minecraft instance management. It brings downloads, accounts, mods and modpacks, multiplayer tools, performance information, and an AI assistant into one application. Keep the familiar layout or enable the new interface preview, then adjust themes, animations, shortcuts, and touch controls.

**This is a source repository.** GitHub's “Download ZIP” downloads source, not an installer. Executable downloads exist only when the maintainers actually publish build artifacts in Releases. This document does not assume an unpublished release or download URL.

## What it offers

| Area | Capabilities |
| --- | --- |
| Instances and accounts | Manage game instances, launch arguments, Java environments, and accounts. |
| Content | Download versions; manage mods, modpacks, resource packs, shaders, worlds, and download tasks. |
| AI assistant | Conversations, attachments, history, model selection, and work modes for investigation, planning, and permitted actions, with visible execution details. |
| Multiplayer and servers | Server management, connection diagnostics, and Terracotta integration, subject to available services and dependencies. |
| Desktop experience | Light/dark themes, languages, animation preferences, touch bindings, shortcuts, a performance overlay, and topmost settings. |
| Experimental tools | Mini World entry points and CDK/direct-link downloads after a reading confirmation, using valid publisher-supported sources. |

Copy and Regenerate stay visible as transparent outlined buttons. Execution details expand and collapse with animation. The new interface preview restyles existing navigation and cards. Documentation languages and application languages are separate; some experimental entries currently appear only in the Simplified Chinese interface.

## Get started

1. If a binary release exists, obtain its complete package from the original project's verified Releases page. Check the publisher, release notes, and published checksums.
2. Extract the entire directory and run `XCL2.exe`. Keep its dependencies, including WebView2 files; copying the executable alone is insufficient.
3. Configure your account, game directory, instance, and Java environment as needed, then launch the selected instance.
4. AI features require a supported provider, model, and your own credentials. Availability, pricing, and data handling depend on that provider.

This source does not include game entitlements, rights to paid resources, or universal CDKs. XCL2 is not affiliated with or endorsed by Mojang, Microsoft, or other game rights holders. Open-source licensing does not replace their terms.

## Build from source

Use a Windows desktop and the .NET 8 SDK. The main project targets `net8.0-windows10.0.19041.0` and declares Windows 10 1809 as its minimum supported platform; individual modules may require more. x86, x64, and ARM64 configurations exist, but not every external component supports every architecture.

The existing Bedrock module requires **`XCL2_PRE_KEY` and `XCL2_REL_KEY`, each containing 96 hexadecimal characters**. Their values are not included. After arranging valid build configuration, run from the repository root:

```powershell
dotnet restore .\XCL2.App\XCL2.App.csproj
dotnet build .\XCL2.App\XCL2.App.csproj -c Release -p:SkipNativePreview=true
```

This skips the separate WinUI preview executable while retaining the WPF interface preview. See the bilingual [build guide](../BUILDING.md) for publishing and limitations. The external configuration requirement means this source has not yet been shown to support an entirely self-contained, reproducible build.

Run relevant checks on Windows:

```powershell
powershell -ExecutionPolicy Bypass -File .\XCL2.App\Tests\Test-AiPresentation.ps1
powershell -ExecutionPolicy Bypass -File .\XCL2.App\Tests\Test-October29Changes.ps1 -IncludeWindowsUi
```

Previous delivery checks were static. A Windows build and desktop verification were not performed in the delivery environment; no passing build status is claimed.

## Documentation

| Document | Purpose |
| --- | --- |
| [Open source and reuse](../../OPEN_SOURCE.md) | License scope, origin notices, branding, and the small-snippet permission; Chinese and English. |
| [Distribution guide](../DISTRIBUTION.md) | Unofficial package labels, notices, and Corresponding Source; Chinese and English. |
| [Contributing](../../CONTRIBUTING.md) | Issues, translations, code, and contribution rights. |
| [Security](../../SECURITY.md) | Vulnerabilities, exposed credentials, and impersonation reports. |
| [Third-party notices](../../THIRD_PARTY_NOTICES.md) | Dependencies and assets retain their own terms. |
| [API reference](../../XCL2.App/Resources/Docs/API_REFERENCE.md) / [examples](../../XCL2.App/Resources/Docs/API_EXAMPLES.md) / [launcher control](../../XCL2.App/Resources/Docs/API_LAUNCHER_CONTROL.md) / [worlds](../../XCL2.App/Resources/Docs/API_WORLDS.md) / [EXE protocol](../../XCL2.App/Resources/Docs/PLUGIN_EXE_PROTOCOL.md) | Plugin API files, in Chinese. |
| [CDK protocol](../CDK-DOWNLOAD.md) / [plugin guide](../../XCL2.App/Resources/Docs/PLUGIN_GUIDE.md) | Publisher and plugin integration, in Chinese. |

## License and unofficial releases

Original material that the project is entitled to license is offered under **GNU AGPL version 3 only (`AGPL-3.0-only`)**, with the notices and additional permission in [OPEN_SOURCE.md](../../OPEN_SOURCE.md). [LICENSE](../../LICENSE) contains the unchanged standard text. Third-party components retain their own licenses.

**Third-party distributions must be clearly identified as UNOFFICIAL.** Identify the distributor and state that its build, modifications, or distribution are not produced, published, or endorsed by the original XCL2 maintainers. Modified packages include a notice inside the archive. An unchanged mirror must preserve the original authorship and label the mirror on its download page and beside the file. A clear filename such as `UNOFFICIAL-XCL2-distributor-version.zip` is a convenient convention; equally clear identification is described in the distribution guide.

The purpose is to reduce impersonation, phishing, fraud, and infringement. Legitimate forks, modifications, commercial distribution, and learning remain welcome. **An ordinary one- or two-statement, non-substantial original snippet may be used with a simple acknowledgment comment under the small-snippet permission.** Complete features, accumulated extracts, and third-party code do not qualify.

Help users understand both where the code originated and who is responsible for the package in front of them.
