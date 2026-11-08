using System.Globalization;
using System.Text.Json;

namespace YubiLatch.App;

// Add a resource for a culture here; service error messages are isolated from the UI presentation.
internal static class L
{
    private static readonly Dictionary<string, string> Strings = Load();
    private static Dictionary<string, string> Load()
    {
        using var stream = typeof(L).Assembly.GetManifestResourceStream("YubiLatch.App.Strings.zh-CN.json")!;
        return JsonSerializer.Deserialize<Dictionary<string, string>>(stream)!;
    }
    public static string T(string key) => Strings.GetValueOrDefault(key, key);
    public static string Format(string key, params object[] values) => string.Format(CultureInfo.CurrentCulture, T(key), values);
}
