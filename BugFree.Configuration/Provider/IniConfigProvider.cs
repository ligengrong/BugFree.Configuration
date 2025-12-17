using System.Reflection;
using System.Text;

namespace BugFree.Configuration.Provider
{
    /// <summary>INI 配置提供者。</summary>
    /// <remarks>
    /// 特性与限制：
    /// - 仅支持“扁平”的键值对（String/数值/布尔/DateTime/枚举等基础类型）；
    /// - 不支持集合（List/Array）、字典（Dictionary）、以及嵌套的复杂对象图；
    /// - 仅序列化可读属性，反序列化仅对具备公共 setter 的属性赋值；
    /// - 节名采用类型名（TConfig），键名为属性名；
    /// - 文本编码采用 UTF-8（无 BOM）。
    /// </remarks>
    internal class IniConfigProvider : ConfigProvider
    {
        /// <summary>单例实例。</summary>
        public static readonly IniConfigProvider Instance = new();

        /// <summary>私有构造函数（使用 <see cref="Instance"/> 单例）。</summary>
        IniConfigProvider() { }
        /// <inheritdoc />
        protected override T Deserialize<T>(String text)
        {
            var iniData = ParseIniContent(text);
            var config = new T();

            var properties = typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance);
            foreach (var prop in properties)
            {
                if (!prop.CanWrite) continue;

                var section = prop.DeclaringType?.Name ?? "General";
                var key = prop.Name;

                if (iniData.TryGetValue(section, out var sectionDict) && sectionDict.TryGetValue(key, out var value))
                {
                    try
                    {
                        object? convertedValue = null;
                        var targetType = Nullable.GetUnderlyingType(prop.PropertyType) ?? prop.PropertyType;
                        if (targetType.IsEnum) convertedValue = Enum.Parse(targetType, value, ignoreCase: true);
                        else convertedValue = Convert.ChangeType(value, targetType);
                        prop.SetValue(config, convertedValue);
                    }
                    catch { /* 类型转换失败，跳过 */ }
                }
            }

            return config;
        }

        /// <inheritdoc />
        protected override String Serialize<T>(T model)
        {
            var sb = new StringBuilder();
            var sections = new Dictionary<String, Dictionary<String, String>>();
            var properties = typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance);
            foreach (var prop in properties)
            {
                if (!prop.CanRead) { continue; }
                if (IsIgnored(prop)) { continue; }
                var section = prop.DeclaringType?.Name ?? "General";
                var key = prop.Name;
                var value = prop.GetValue(model)?.ToString() ?? String.Empty;
                if (!sections.ContainsKey(section)) { sections[section] = new Dictionary<String, String>(); }
                sections[section][key] = value;
            }

            foreach (var section in sections)
            {
                sb.AppendLine($"[{section.Key}]");
                foreach (var kvp in section.Value)
                {
                    sb.AppendLine($"{kvp.Key}={kvp.Value}");
                }
                sb.AppendLine();
            }
            return sb.ToString();
        }

        /// <summary>解析 INI 文本到分节字典。</summary>
        /// <param name="content">INI 文本。</param>
        /// <returns>按节名与键名组织的字典。</returns>
        static Dictionary<String, Dictionary<String, String>> ParseIniContent(String content)
        {
            var result = new Dictionary<String, Dictionary<String, String>>(StringComparer.OrdinalIgnoreCase);
            var currentSection = "General";

            using var sr = new StringReader(content);
            String? line;
            while ((line = sr.ReadLine()) != null)
            {
                var trimmed = line.Trim();
                if (String.IsNullOrEmpty(trimmed) || trimmed.StartsWith(';') || trimmed.StartsWith('#'))
                    continue;

                // 节名
                if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
                {
                    currentSection = trimmed[1..^1];
                    if (!result.ContainsKey(currentSection))
                        result[currentSection] = new Dictionary<String, String>(StringComparer.OrdinalIgnoreCase);
                    continue;
                }

                // 键值对
                var equalIndex = trimmed.IndexOf('=');
                if (equalIndex > 0)
                {
                    var key = trimmed[..equalIndex].Trim();
                    var value = trimmed[(equalIndex + 1)..].Trim();
                    if (!result.ContainsKey(currentSection))
                        result[currentSection] = new Dictionary<String, String>(StringComparer.OrdinalIgnoreCase);
                    result[currentSection][key] = value;
                }
            }

            return result;
        }
    }
}
