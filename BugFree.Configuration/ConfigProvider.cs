using BugFree.Configuration.Provider;
using BugFree.Security;

using System.Collections.Concurrent;
using System.ComponentModel;
using System.Reflection;
using System.Runtime.Serialization;
using System.Text;
using System.Text.Json.Serialization;
using System.Xml.Serialization;

using YamlDotNet.Serialization;

namespace BugFree.Configuration
{
    /// <summary>配置提供者抽象基类（模板方法模式）。</summary>
    /// <remarks>
    /// 统一实现 Load/Save 的通用流程：
    /// 1. 解析完整文件路径，确保目录存在；
    /// 2. 读取/写入文件（可覆盖编码 <see cref="UTF8Encoding"/>）；
    /// 3. 按需加解密（<see cref="Encrypt"/> / <see cref="Decrypt"/>）；
    /// 4. 调用各具体提供者实现的序列化/反序列化（<see cref="Serialize"/> / <see cref="Deserialize"/>）。
    /// 子类只需关心文本与模型的互转细节。
    /// </remarks>
    public abstract class ConfigProvider : IDisposable
    {
        /// <summary>注释头部缓存（按模型类型与提供者类型缓存）。</summary>
        static readonly ConcurrentDictionary<(Type type, ConfigProviderType? provider), String> _CommentHeaderCache = new();
        #region 属性
        /// <summary>提供者名称（类型名去掉后缀“ConfigProvider”）。</summary>
        public String? Name => GetType().Name.Replace(nameof(ConfigProvider), string.Empty);
        /// <summary>是否新建（当目标文件不存在或内容为空时为 true）。</summary>
        public Boolean IsNew { get; protected set; }
        /// <summary>默认 UTF-8 编码（无 BOM）。</summary>
        protected UTF8Encoding UTF8Encoding => new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        #endregion

        /// <summary>加载配置到模型。</summary>
        /// <typeparam name="T">模型类型。</typeparam>
        /// <param name="attribute">配置特性（包含 Provider/Path/Name/加密等信息）。</param>
        /// <param name="path">可选路径；为空则使用 <see cref="ConfigAttribute.GetFullPath"/>。</param>
        /// <returns>模型实例（当文件不存在或为空时返回默认新实例）。</returns>
        public T Load<T>(ConfigAttribute attribute, String? path = null) where T : new()
        {
            var filePath = path ?? attribute.GetFullPath();
            if (!File.Exists(filePath)) { IsNew = true; return new T(); }
            const Int32 maxRetries = 5;
            for (var i = 0; i <= maxRetries; i++)
            {
                try
                {
                    // 安全读取
                    using var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                    using var reader = new StreamReader(fileStream, UTF8Encoding);
                    var context = reader.ReadToEnd();
                    if (String.IsNullOrWhiteSpace(context)) { IsNew = true; return new T(); }
                    var plain = Decrypt(attribute, context);
                    var model = Deserialize<T>(plain) ?? new T();
                    IsNew = false;
                    return model;
                }
                catch (IOException ex)
                {
                    if (ex.HResult == -2147024864 && i < maxRetries)// 0x80070020 ERROR_SHARING_VIOLATION
                    {
                        // 文件被锁定时等待重试
                        Thread.Sleep(100);
                        continue;
                    }
                    throw;
                }
            }

            return new T();
        }

        /// <summary>保存模型实例。</summary>
        /// <typeparam name="T">模型类型。</typeparam>
        /// <param name="model">模型实例。</param>
        /// <param name="attribute">配置特性（包含 Provider/Path/Name/加密等信息）。</param>
        /// <param name="path">可选路径；为空则使用 <see cref="ConfigAttribute.GetFullPath"/>。</param>
        /// <returns>是否保存成功。</returns>
        public Boolean Save<T>(T model, ConfigAttribute attribute, String? path = null)
        {
            var filePath = path ?? attribute.GetFullPath();
            if (String.IsNullOrEmpty(filePath) || model == null) return false;

            // 跨进程安全写入：先写入临时文件（同目录同卷），再原子替换目标文件，尽量避免读到半文件/被其他进程占用
            var tmp = String.Empty;
            try
            {
                var dir = Path.GetDirectoryName(filePath);
                if (!String.IsNullOrEmpty(dir)) { Directory.CreateDirectory(dir); }
                tmp = Path.Combine(dir ?? Path.GetTempPath(), $"{Path.GetFileName(filePath)}.{Guid.NewGuid():N}.tmp");

                var plain = Serialize(model);
                plain = InjectComments<T>(attribute, plain);
                var cipher = Encrypt(attribute, plain);
                File.WriteAllText(tmp, cipher, UTF8Encoding);
                // 安全替换目标文件
                File.Move(tmp, filePath, overwrite: true);
                IsNew = false;
                return true;
            }
            catch { throw; }
            finally { if (!String.IsNullOrEmpty(tmp) && File.Exists(tmp)) File.Delete(tmp); }
        }

        /// <summary>释放资源。</summary>
        public virtual void Dispose() { }
        /// <summary>序列化模型为文本（由具体提供者实现）。</summary>
        protected abstract String Serialize<T>(T model);
        /// <summary>反序列化文本为模型（由具体提供者实现）。</summary>
        protected abstract T Deserialize<T>(String text) where T : new();

        /// <summary>加密明文（当 <see cref="ConfigAttribute.IsEncrypted"/> 为 true 时）。</summary>
        protected virtual String Encrypt(ConfigAttribute attribute, String plainText)
            => attribute.IsEncrypted ? plainText.EncryptSymmetric(SymmetricAlgorithm.AesGcm, attribute.Secret!) : plainText;
        /// <summary>解密密文（当 <see cref="ConfigAttribute.IsEncrypted"/> 为 true 时）。</summary>
        protected virtual String Decrypt(ConfigAttribute attribute, String cipherText)
            => attribute.IsEncrypted ? cipherText.DecryptSymmetric(attribute.Secret!) : cipherText;

        /// <summary>判断属性是否被忽略（不参与序列化/反序列化）。</summary>
        /// <param name="property">属性信息。</param>
        /// <returns>是否忽略。</returns>
        protected virtual Boolean IsIgnored(PropertyInfo property)
        {
            if (property.GetCustomAttribute<XmlIgnoreAttribute>() != null) { return true; }
            if (property.GetCustomAttribute<IgnoreDataMemberAttribute>() != null) { return true; }
            if (property.GetCustomAttribute<JsonIgnoreAttribute>() != null) { return true; }
            if (property.GetCustomAttribute<YamlIgnoreAttribute>() != null) { return true; }
            return false;
        }

        /// <summary>为配置文本注入属性说明注释头。</summary>
        /// <typeparam name="T">模型类型。</typeparam>
        /// <param name="attribute">配置特性。</param>
        /// <param name="plain">未加密的文本内容。</param>
        /// <returns>注入注释后的文本内容。</returns>
        String InjectComments<T>(ConfigAttribute attribute, String plain)
        {
            if (attribute.IsEncrypted) { return plain; }
            if (String.IsNullOrEmpty(plain)) { return plain; }

            var header = _CommentHeaderCache.GetOrAdd((typeof(T), attribute.Provider), (key) =>
            {
                var properties = typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance);
                if (null == properties || 0 >= properties.Length) { return String.Empty; }
                var sb = new StringBuilder();
                foreach (var property in properties)
                {
                    if (!property.CanRead) { continue; }
                    if (IsIgnored(property)) { continue; }
                    var desc = property.GetCustomAttribute<DescriptionAttribute>();
                    if (desc == null || String.IsNullOrWhiteSpace(desc.Description)) { continue; }
                    switch (key.provider)
                    {
                        case ConfigProviderType.Ini:
                            sb.Append("; ").Append(property.Name).Append(" : ").AppendLine(desc.Description);
                            break;
                        case ConfigProviderType.Xml:
                            sb.Append("<!-- ").Append(property.Name).Append(" : ").Append(desc.Description).AppendLine(" -->");
                            break;
                        case ConfigProviderType.Json:
                            sb.Append("// ").Append(property.Name).Append(" : ").AppendLine(desc.Description);
                            break;
                        case ConfigProviderType.Yaml:
                            sb.Append("# ").Append(property.Name).Append(" : ").AppendLine(desc.Description);
                            break;
                    }
                }
                if (sb.Length > 0) { sb.AppendLine(); }
                return sb.ToString();
            });
            if (String.IsNullOrEmpty(header)) { return plain; }

            //XML 声明必须是文档第一行（若存在）。注释插入到 XML 声明之后，避免生成非法 XML。
            if (attribute.Provider == ConfigProviderType.Xml && plain.StartsWith("<?xml", StringComparison.Ordinal))
            {
                var endDeclIndex = plain.IndexOf("?>", StringComparison.Ordinal);
                if (endDeclIndex >= 0)
                {
                    var insertIndex = endDeclIndex + 2;
                    if (insertIndex < plain.Length && plain[insertIndex] == '\r') { insertIndex++; }
                    if (insertIndex < plain.Length && plain[insertIndex] == '\n') { insertIndex++; }

                    if (insertIndex < plain.Length && plain[insertIndex] != '\r' && plain[insertIndex] != '\n')
                    {
                        return plain.Insert(insertIndex, Environment.NewLine + header);
                    }
                    return plain.Insert(insertIndex, header);
                }
            }

            return String.Concat(header, plain);
        }
        /// <summary>根据配置特性创建具体的配置提供者实例。</summary>
        /// <param name="provider">提供者类型。</param>
        /// <returns>配置提供者实例。</returns>
        public static ConfigProvider Create(ConfigProviderType? provider)
        {
            return provider switch
            {
                ConfigProviderType.Ini => IniConfigProvider.Instance,
                ConfigProviderType.Xml => XmlConfigProvider.Instance,
                ConfigProviderType.Json => JsonConfigProvider.Instance,
                ConfigProviderType.Yaml => YamlConfigProvider.Instance,
                _ => throw new NotSupportedException($"不支持的配置提供者 {provider}")
            };
        }
    }
}
