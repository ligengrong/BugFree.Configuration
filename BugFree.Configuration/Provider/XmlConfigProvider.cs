using System.ComponentModel;
using System.Reflection;
using System.Text;
using System.Xml;
using System.Xml.Serialization;

using YamlDotNet.Serialization;

namespace BugFree.Configuration.Provider
{
    /// <summary>XML 配置提供者（使用 XmlSerializer）。</summary>
    /// <remarks>
    /// 特性与限制：
    /// - 支持大多数 POCO、以及集合（List/Array）；
    /// - Dictionary不被直接支持，可通过自定义桥接类型/属性（例如将字典映射为条目列表）实现；
    /// - 要求公共无参构造；接口/抽象类型属性无法反序列化；
    /// - 不支持对象图中的循环引用；
    /// - 可用 [XmlIgnore] 排除成员；
    /// - 默认输出 UTF-8（无 BOM），带 XML 声明，缩进 2 空格，Windows 换行符；
    /// - 日期/时间等类型的格式遵循 XmlSerializer 的默认行为。
    /// </remarks>
    internal class XmlConfigProvider : ConfigProvider
    {
        /// <summary>XML 文件默认使用 UTF-8（无 BOM）。</summary>
        protected new UTF8Encoding UTF8Encoding => new UTF8Encoding(false);
        /// <inheritdoc />
        protected override string Serialize<T>(T model)
        {
            var serializer = new XmlSerializer(typeof(T));
            var settings = new XmlWriterSettings
            {
                Indent = true,
                IndentChars = "  ",
                NewLineChars = "\r\n",
                NewLineHandling = NewLineHandling.Replace,
                OmitXmlDeclaration = false,
                Encoding = new UTF8Encoding(false)
            };

            using var ms = new MemoryStream();
            using (var writer = XmlWriter.Create(ms, settings))
            {
                serializer.Serialize(writer, model);
            }
            return Encoding.UTF8.GetString(ms.ToArray());
        }

        /// <inheritdoc />
        protected override T Deserialize<T>(string text)
        {
            var serializer = new XmlSerializer(typeof(T));
            using var reader = new StringReader(text);
            using var xr = XmlReader.Create(reader, new XmlReaderSettings
            {
                IgnoreComments = true,
                IgnoreProcessingInstructions = true,
                IgnoreWhitespace = false
            });
            var cfg = serializer.Deserialize(xr);
            return cfg is T t ? t : new T();
        }
    }
}
