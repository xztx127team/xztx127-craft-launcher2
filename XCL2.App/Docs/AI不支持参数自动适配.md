# AI 参数兼容重试

当接口明确返回不支持某个已发送的可选参数时，启动器会省略该参数，在同一接口、同一模型和同一密钥上重试。例如：

```text
claude-sonnet-5-5 does not support non-default temperature
```

第一次请求失败后，重试请求不再包含 `temperature`。该接口之后的模型测试、聊天、预热和摘要请求也会省略它，重新打开启动器后仍保留这项记忆。

记忆按接口身份及规范化地址区分。同一接口的不同模型和密钥共享记忆；其他接口、地址或查询参数不同的接口不会被修改。缓存不包含真实接口地址、密钥或对话内容。

支持自动适配的可选字段包括 `temperature`、`reasoning_effort`、`stream_options`、`stream` 和 `tools`。停用流式参数时，也会省略依赖它的 `stream_options`。输出长度的旧字段 `max_tokens` 被拒绝时，改用 `max_completion_tokens` 保留已配置的长度上限；若两种字段都不可用，保持原配额约束并显示错误。

只有接口明确拒绝实际发送的字段时才学习。密钥错误、额度不足、服务器故障、普通参数范围或类型错误仍按原来的逻辑处理。已收到部分回复时不自动重发；连续拒绝多个字段时逐项适配，并遵守原有总超时和尝试次数上限。

## 验证

- 新增错误识别、持久缓存与模拟 HTTP 回归，覆盖原始报错、逐项删除、重启、接口隔离、流式回复及输出长度兼容。
- Windows 上可执行 `dotnet run --project Tests/AiHttpRegression/AiHttpRegression.csproj` 验证完整模拟请求链路。
- 当前环境没有 .NET SDK，C# 回归尚未执行；源码静态检查不替代编译和真实供应商接口验证。
