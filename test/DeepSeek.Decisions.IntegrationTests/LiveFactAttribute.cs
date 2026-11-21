using Xunit;

namespace DeepSeek.Decisions.IntegrationTests;

public sealed class LiveFactAttribute : FactAttribute
{
    public LiveFactAttribute()
    {
        if (ReadApiKey() is null && Environment.GetEnvironmentVariable("DEEPSEEK_REQUIRE_LIVE_TESTS") != "true")
            Skip = "未配置 DeepSeek 在线测试凭据。";
    }

    internal static string GetApiKey() => ReadApiKey()
        ?? throw new InvalidOperationException("在线测试需要 DEEPSEEK_API_KEY 或 deepseek-api-key。");

    private static string? ReadApiKey()
    {
        foreach (var name in new[] { "DEEPSEEK_API_KEY", "deepseek-api-key" })
        {
            var key = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrWhiteSpace(key) && key != "test-key")
                return key;
        }

        return null;
    }
}
