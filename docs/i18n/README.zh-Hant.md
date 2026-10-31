<p align="center"><img src="../assets/xcl2-mark.png" alt="XCL2：藍青色 X 與啟動箭頭組成的專案圖示" width="128" height="128"></p>
<h1 align="center">XCL2</h1>
<p align="center"><strong>讓遊戲啟動、執行個體管理與 AI 協作，回到一個清楚的桌面。</strong></p>
<p align="center">Windows · .NET 8 · WPF · AGPL-3.0</p>
<p align="center"><a href="../../README.md">简体中文</a> · <strong>繁體中文</strong> · <a href="README.en.md">English</a> · <a href="README.ru.md">Русский</a> · <a href="README.ja.md">日本語</a></p>

---

XCL2 是面向 Windows 的獨立遊戲啟動器，以 Minecraft 執行個體管理為中心，整合版本下載、帳戶、模組與整合包、連線工具、效能資訊及 AI 助手。你可以保留經典版面，也可以開啟新介面預覽，調整佈景主題、動畫、快速鍵及觸控操作。

**這是原始碼儲存庫。** GitHub 的「Download ZIP」下載的是原始碼，不是安裝程式。只有維護者實際在 Releases 發布了建置成品，才有對應的程式下載；本文件不預設尚未發布的版本或網址。

## 主要功能

| 方向 | 現有能力 |
| --- | --- |
| 執行個體與帳戶 | 管理遊戲執行個體、啟動參數、Java 環境及帳戶。 |
| 內容管理 | 下載版本，管理模組、整合包、資源包、光影、世界與下載工作。 |
| AI 助手 | 對話、附件、歷史紀錄、模型選擇，以及協助調查、規劃和授權操作的工作模式；可查看執行過程。 |
| 連線與伺服器 | 伺服器管理、連線診斷與 Terracotta 整合；依服務及本機相依元件而定。 |
| 桌面體驗 | 深淺佈景主題、多語言、動畫偏好、觸控按鍵、快速鍵、效能浮動視窗及置頂設定。 |
| 實驗入口 | 迷你世界相關入口與 CDK／直接連結下載；需閱讀確認，並使用發行者支援的有效來源。 |

「複製」與「重新生成」採常駐透明描邊按鈕；執行過程支援展開及收合動畫。新介面預覽重新整理現有導覽與卡片。文件語言與程式介面語言獨立；部分實驗入口目前只在簡體中文介面顯示。

## 開始使用

1. 若已有程式發行版，從已確認屬於原專案的 Releases 取得完整檔案包，核對發行者、版本說明及校驗值。
2. 解壓縮整個目錄後執行 `XCL2.exe`。WebView2 等相依檔案也必須保留，不要只複製 EXE。
3. 依需要設定帳戶、遊戲目錄、執行個體及 Java，再選擇執行個體啟動。
4. AI 功能需自行設定支援的服務、模型與憑證；可用性、價格及資料處理方式由服務提供者決定。

原始碼不附遊戲授權、付費資源使用權或通用 CDK。XCL2 與 Mojang、Microsoft 或其他遊戲權利人沒有官方隸屬或背書關係。開源不取代第三方的使用條件。

## 從原始碼建置

使用 Windows 桌面與 .NET 8 SDK。主專案目標為 `net8.0-windows10.0.19041.0`，最低平台宣告為 Windows 10 1809；個別模組可能另有需求。專案包含 x86、x64、ARM64 組態，不代表所有外部元件都有對應版本。

現有基岩版模組需要 **`XCL2_PRE_KEY` 與 `XCL2_REL_KEY`，各為 96 位十六進位值**；原始碼不附這些值。準備合法可用的建置設定後，於儲存庫根目錄執行：

```powershell
dotnet restore .\XCL2.App\XCL2.App.csproj
dotnet build .\XCL2.App\XCL2.App.csproj -c Release -p:SkipNativePreview=true
```

這會略過獨立 WinUI 預覽程式的建置，但保留 WPF 內的新介面預覽。詳見中英雙語的[建置指南](../BUILDING.md)。目前仍需要外部建置設定，不能將公開原始碼視為已完成無額外設定的可重現建置。

Windows 回歸檢查：

```powershell
powershell -ExecutionPolicy Bypass -File .\XCL2.App\Tests\Test-AiPresentation.ps1
powershell -ExecutionPolicy Bypass -File .\XCL2.App\Tests\Test-October29Changes.ps1 -IncludeWindowsUi
```

先前交付只有靜態檢查，交付環境未完成 Windows 編譯與實機驗證；本文件不宣稱建置已通過。

## 文件

| 文件 | 內容 |
| --- | --- |
| [開源與引用](../../OPEN_SOURCE.md) | AGPL 範圍、來源標示、品牌及小片段額外許可，中英雙語。 |
| [第三方發行指南](../DISTRIBUTION.md) | 非官方標識、聲明範本與對應原始碼，中英雙語。 |
| [貢獻指南](../../CONTRIBUTING.md)／[安全回報](../../SECURITY.md) | 問題回報、翻譯、程式貢獻與漏洞處理。 |
| [第三方說明](../../THIRD_PARTY_NOTICES.md) | 相依元件與素材保留各自條款。 |
| [API 開放檔案](../../XCL2.App/Resources/Docs/API_REFERENCE.md)／[範例](../../XCL2.App/Resources/Docs/API_EXAMPLES.md)／[啟動器控制](../../XCL2.App/Resources/Docs/API_LAUNCHER_CONTROL.md)／[存檔](../../XCL2.App/Resources/Docs/API_WORLDS.md)／[EXE 協定](../../XCL2.App/Resources/Docs/PLUGIN_EXE_PROTOCOL.md) | 外掛 API 文件，簡體中文。 |
| [CDK 協定](../CDK-DOWNLOAD.md)／[外掛指南](../../XCL2.App/Resources/Docs/PLUGIN_GUIDE.md) | 發行者及外掛整合說明，簡體中文。 |

## 開源與第三方分發

專案有權授權的原創部分採用 **GNU AGPL 第 3 版，僅此版本（`AGPL-3.0-only`）**，並附[來源聲明與額外許可](../../OPEN_SOURCE.md)。[LICENSE](../../LICENSE) 保留標準英文全文；第三方元件沿用各自授權。

**第三方分發必須顯著標示「非官方／UNOFFICIAL」**，說明發行者，並聲明其建置、修改或分發並非原 XCL2 維護者製作、發布或背書。修改包須附包內聲明；未修改的鏡像須保留原作者歸屬，並在下載頁及檔案旁標明鏡像身分。可採用 `UNOFFICIAL-XCL2-發行者-版本.zip`；等效的清楚標示方式見發行指南。

這些要求旨在減少冒充、釣魚、詐騙與侵權，並不將正常 fork、修改、收費分發或學習稱為「盜版」。**普通的一兩條邏輯敘述、非實質性原創片段，可依小片段額外許可，只附一條致謝註解使用。** 完整功能、累積拆分複製及第三方程式碼不適用此例外。

歡迎改進，也請讓使用者清楚知道：程式碼來自哪裡，目前這個檔案包由誰負責。
