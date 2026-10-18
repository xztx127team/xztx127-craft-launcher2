using System.Reflection;

namespace XCL2.App.BedrockDecode
{
    internal static class _DEFINE_REF2
    {
        public static readonly byte[] pre = Load("XclBedrockPre");
        public static readonly byte[] rel = Load("XclBedrockRel");

        private static byte[] Load(string key)
        {
            foreach (var attribute in typeof(_DEFINE_REF2).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>())
            {
                if (attribute.Key == key && !string.IsNullOrEmpty(attribute.Value))
                {
                    return Convert.FromHexString(attribute.Value);
                }
            }

            throw new InvalidOperationException($"构建时未注入 {key}，请先设置环境变量后重新编译。");
        }
    }
}
