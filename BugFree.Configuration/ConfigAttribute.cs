namespace BugFree.Configuration
{
    /// <summary>配置特性</summary>
    /// <remarks>
    /// 声明配置模型使用哪一种配置提供者，以及所需要的文件名和分类名。
    /// 如未指定提供者，则使用全局默认，此时将根据全局代码配置或环境变量配置使用不同提供者，实现配置信息整体转移。
    /// </remarks>
    public class ConfigAttribute : Attribute
    {
        /// <summary>
        /// 提供者。内置 ini/xml/json（见 <see cref="ConfigProviderType"/>）。
        /// </summary>
        public ConfigProviderType? Provider { get; set; }
        /// <summary>配置名。可以是文件名或分类名</summary>
        public String Name { get; set; }
        /// <summary>配置路径(相对路径)。一般不指定，使用全局默认</summary>
        public String? Path { get; set; }
        /// <summary>是否加密</summary>
        public Boolean IsEncrypted { get; set; }
        /// <summary>加密密钥</summary>
        public String? Secret { get; set; }
        String? _FilePath;
        /// <summary>指定配置名</summary>
        /// <param name="name">配置名。可以是文件名或分类名</param>
        /// <param name="provider">提供者。内置 ini/xml/json，一般不指定，使用全局默认</param>
        /// <param name="path">配置路径。一般不指定，使用全局默认</param>
        public ConfigAttribute(String name, ConfigProviderType provider = ConfigProviderType.Json, String? path = null, Boolean isencrypted = false, String? secret = null)
        {
            Provider = provider;
            Name = name;
            Path = path;
            IsEncrypted = isencrypted;
            if (IsEncrypted && string.IsNullOrWhiteSpace(secret)) { throw new Exception($"启用加密{nameof(secret)}未提供"); }
            Secret = secret;
        }
        public ConfigAttribute() { }
        public string GetFullPath()
        {
            if (!String.IsNullOrWhiteSpace(_FilePath)) { return _FilePath; }
            // 解析文件路径
            var basePath = Path ?? "./config";
            var fileName = $"{Name}.{Provider}";
            _FilePath = System.IO.Path.GetFullPath(System.IO.Path.Combine(basePath, fileName));
            // 确保目录存在
            var directory = System.IO.Path.GetDirectoryName(_FilePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory)) { Directory.CreateDirectory(directory); }
            return _FilePath;
        }
    }

    /// <summary>配置提供者类型。</summary>
    /// <remarks>
    /// 能力与限制简述：
    /// - Ini：仅适合简单“扁平”的键值对，不支持集合、字典、复杂嵌套类型；仅能可靠处理 string、数值、bool、DateTime、枚举等基础类型；
    /// - Xml（XmlSerializer）：支持大多数 POCO 与集合；不支持 Dictionary<TKey,TValue>（需自定义桥接类型）、接口/抽象类型、缺少公共无参构造的类型、循环引用；
    /// - Json（System.Text.Json）：支持复杂对象/集合/字典；默认不支持接口/抽象类型的多态反序列化、循环引用（需配置/转换器），只序列化可读写公共属性。
    /// </remarks>
    public enum ConfigProviderType
    {
        /// <summary>
        /// ini 配置（简单键值映射，不支持集合和嵌套复杂类型）。
        /// 适用：原始类型（string/数值/布尔/DateTime/枚举）。
        /// 限制：不支持 List/Array/Dictionary/自定义对象的嵌套；只写出可读属性，读取时仅处理有 setter 的属性。
        /// </summary>
        Ini,
        /// <summary>
        /// xml 配置（基于 XmlSerializer）。
        /// 适用：大多数 POCO 与集合（List/Array）。
        /// 限制：Dictionary<TKey,TValue> 不被直接支持；接口/抽象类型属性不能反序列化；要求公共无参构造；不支持循环引用；可用 [XmlIgnore] 排除成员。
        /// </summary>
        Xml,
        /// <summary>
        /// json 配置（基于 System.Text.Json）。
        /// 适用：复杂对象/集合/字典。
        /// 限制：接口/抽象类型需要自定义转换器或多态配置；默认不处理循环引用；只序列化可读写公共属性；只反序列化有公共 setter 的属性。
        /// </summary>
        Json,
        /// <summary>
        /// yaml 配置（基于 YamlDotNet）。
        /// 适用：复杂对象/集合/字典与嵌套对象，文件扩展名建议 .yaml/.yml。
        /// 限制：默认不保留注释；接口/抽象类型需自定义转换器或多态支持；命名约定默认与模型一致（可通过命名约定配置调整）。
        /// </summary>
        Yaml,
    }
}
