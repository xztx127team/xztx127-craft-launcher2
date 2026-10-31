namespace XCL2.App.Services;

public sealed record ExperimentalFeature(string Id, string Title, string Description, string SeedHash);

public static class ExperimentalFeatureCatalog
{
    public static IReadOnlyList<ExperimentalFeature> All { get; } = new ExperimentalFeature[]
    {
        new("multi-loader", "安装多个加载器", "组合安装加载器并保留实例安装结果。", "eadb6dd92737498cd79b39a6194594e09a5c078715b71e3239ab116fc1881f53"),
        new("mini-world", "迷你世界实验入口", "打开独立的下载与启动面板。", "6d90c646c3ec3ef4989ee594d6dcf721735d357cef4623da7cd7a5d151e8ada1"),
        new("crash", "模拟 XCL 崩溃", "生成完整的崩溃报告，启动器继续运行。", "cccb9bce1d485ed5a6ef16ec6a6710979762fe7c849b8b681cff7654450b0072"),
        new("worker-crash", "后台线程异常", "捕获后台任务异常并展示诊断链。", "b511eff7eac9fd51bf01dc6c789d0178436c907042b5379f6535bbed545a680f"),
        new("timeout", "网络超时演练", "验证超时异常的错误分类和处理建议。", "e84247ce5b3ed3eb227a12886a58f0c4744a77f1ae023ae6867168482a5e1504"),
        new("json-damage", "损坏 JSON 演练", "解析损坏的内存样本，检查错误定位。", "8719a77b65ba2a7164bae02ec23d60eba1319686e28322d99ac212b0ac9c77ab"),
        new("integrity", "下载完整性演练", "对两份不同内容计算哈希，验证校验失败报告。", "f5b872852d01b8aff952836b40a54457865b16bb3d311e0b5e02d5ee3819f3ac"),
        new("memory", "即时内存整理", "整理当前桌面会话的进程工作集。", "48ed181cf7c9153acaf06082aca7201544dd0ab1cf6753317580c2ebc9add6c5"),
        new("memory-snapshot", "内存快照", "查看物理内存、托管堆和启动器工作集。", "22616027ac35b178605a75fe9fb0581fb8c2f03354ec444b2bea84fce7d2f271"),
        new("contrast", "深浅主题对比度检查", "检测当前文字与背景组合的对比度。", "f4ebf750e585313b56e251adaa789409b3c76d1a8de9f56042efd9120a1b24ed"),
        new("dispatcher", "界面响应计时", "测量五次 UI 调度的响应延迟。", "0c85a2c0aed1efb54693a8b4f811ea98363f3acc3405323f244443c89d5f2e59"),
        new("parallel", "并行计算探针", "并行执行四份有限的计算，显示耗时与校验值。", "322c7e1be7c3b11b199580fe6578a2e5bb39dd356c87886bc98a1b72775d13b6"),
        new("write-probe", "数据目录写入探针", "创建、读回并删除一份临时测试文件。", "600a7f762d253918d6b23cb27028d0704368b84070037ebd7c3b360a59b794ae"),
        new("rendering", "渲染能力检测", "显示 WPF 渲染级别、屏幕缩放与架构。", "287d8e36e6b46637ca3db2cde5df90d96fce4c4206679b0385dfbcba5b5c9349"),
        new("exception-chain", "嵌套异常分析", "演示内层异常的独立识别与完整详情。", "a760eb7158b34946847b5dc0a8f858f88be8ef092623d68f7b7bb322fd0c290d"),
        new("clipboard", "剪贴板往返检查", "复制专用测试文本，读回后检查结果。", "9b4d846e878f7ab47de016b1f8a418f3f3c451e90f37e21d80dc76e1cfe392fa"),
        new("animation", "动画实验台", "预览十五种启动器特效，F1 随时恢复。", "4a0c5aea9d310adc21458c5efd9c646ffadec7f6d26aa7a84d38641ca79beca4"),
        new("architecture", "联机本体架构检查", "读取所选陶瓦本体的 PE 架构并检查系统兼容性。", "749f57e912f09a3c4e1d8f42bec542f6b047afe4a6d0fca6aa672a1aab09dc09"),
    };
}
