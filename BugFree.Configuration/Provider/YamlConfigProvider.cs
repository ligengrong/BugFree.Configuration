using System;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace BugFree.Configuration.Provider
{
    /// <summary>YAML 配置提供者。</summary>
    /// <remarks>
    /// 特性：
    /// - 支持复杂对象/集合（List/Array/Dictionary）与嵌套对象；
    /// - 仅序列化可读属性，反序列化仅对具备公共 setter 的属性赋值；
    /// - 默认保持属性名大小写（与模型一致），不做命名转换；
    /// - 文本编码由基类负责（UTF-8 无 BOM）；是否加密由基类 Encrypt/Decrypt 控制；
    /// - 不保留注释。
    /// </remarks>
    internal class YamlConfigProvider : ConfigProvider
    {
        /// <inheritdoc />
        protected override T Deserialize<T>(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return new T();

            var deserializer = new DeserializerBuilder()
                .WithNamingConvention(NullNamingConvention.Instance) // 保持属性名
                .IgnoreUnmatchedProperties()
                .Build();

            var model = deserializer.Deserialize<T>(text);
            return model is null ? new T() : model;
        }

        /// <inheritdoc />
        protected override string Serialize<T>(T model)
        {
            var serializer = new SerializerBuilder()
                .WithNamingConvention(NullNamingConvention.Instance) // 保持属性名
                .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitDefaults) // 省略默认值
                .Build();

            // 直接序列化为 YAML 文本，由基类负责加密/保存
            return serializer.Serialize(model);
        }
    }
}
