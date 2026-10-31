# 迷你世界 CDK 下载接口

这是 XCL2 新增的可配置接口协议。只有发布者实现兼容接口后才能直接兑换；以下域名仅为格式示例，不是真实可用下载源。

## 版本目录

开放 JSON 版本目录可增加 `cdkRedeemUrl`。`url` 可提供公开文件直链；如果仅支持 CDK，可留空并显式提供合法 `fileName`。

```json
{
  "versions": [
    {
      "id": "community-1",
      "name": "发布者的社区版本",
      "version": "1.0",
      "sourcePage": "https://publisher.example.invalid/release/1",
      "url": "",
      "fileName": "client.7z",
      "cdkRedeemUrl": "https://publisher.example.invalid/api/redeem"
    }
  ]
}
```

## 请求

用户在界面确认服务域名、填写 CDK 并点击加入下载队列后，向指定 HTTPS 接口发送一次 POST，`Content-Type: application/json`：

```json
{"cdk":"用户填写的兑换码","versionId":"community-1"}
```

无 Cookies、无启动器账户凭证；不将 CDK 放进查询字符串，不缓存 CDK，不自动重试，不跟随兑换 POST 的 HTTP 重定向。接口请求与响应读取总计最多等待 30 秒。关闭兑换窗口取消请求；已经被服务器接收的兑换不能由客户端撤回，所以超时后应先核实兑换码状态。

## 响应

成功时返回 HTTP 2xx 和 JSON，可直接返回对象，或者包装在 `data` 中：

```json
{
  "data": {
    "url": "https://downloads.example.invalid/client.7z?download_token=example",
    "fileName": "client.7z",
    "sha256": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
    "mirrors": ["https://mirror.example.invalid/client.7z"]
  }
}
```

`downloadUrl` 可作为 `url` 的别名。`sha256` 和 `mirrors` 可省略；提供时必须分别是 64 位十六进制值和最多 16 个 HTTPS 镜像。响应上限 64 KiB、JSON 深度上限 12。`fileName` 优先采用返回值，否则使用界面填写值，再尝试从直链提取。

下载文件名只允许现有下载器支持的 EXE、ZIP、7Z 及对应 `.001`/`.002` 分卷形式，拒绝路径穿越、Windows 保留名和网页格式。SHA-256 由发布者提供时用于校验；没有提供时只做现有格式与传输检查。文件加入原下载队列并保留版本绑定，安装包不会自动执行。兑换直链和用户粘贴的直链不会被预先发出探测 GET，以免提前消耗一次性下载权限；直接对实际下载内容做校验。已有同版本进行中的任务时会先提示，避免重复兑换。

兑换接口返回认证失败、重定向、HTML、畸形 JSON、过大响应或非法地址时显示错误，不跳转浏览器。原有“发布者来源页”等按钮仍需用户主动点击。
