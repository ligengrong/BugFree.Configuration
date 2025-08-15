using System.Text.Json;
using System.Text.Json.Serialization;

namespace BugFree.Configuration.Provider
{
    /// <summary>JSON 配置提供者（使用 System.Text.Json）。</summary>
    /// <remarks>
    /// 特性与限制：
    /// - 支持复杂对象、集合、字典；
    /// - 默认大小写不敏感；忽略 null；缩进输出；
    /// - 不支持接口/抽象类型的多态反序列化（可通过自定义转换器或 JsonPolymorphism 特性配置）;
    /// - 默认不处理循环引用；
    /// - 仅序列化/反序列化公共可读写属性；字段与只读属性不会被处理。
    /// </remarks>
    internal class JsonConfigProvider : ConfigProvider
    {
        /// <summary>默认的 JSON 序列化选项。</summary>
        static readonly JsonSerializerOptions _options = new()
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        /// <inheritdoc />
        protected override T Deserialize<T>(string text)
            => JsonSerializer.Deserialize<T>(text, _options);

        /// <inheritdoc />
        protected override string Serialize<T>(T model)
            => JsonSerializer.Serialize(model, _options);
    }
}
