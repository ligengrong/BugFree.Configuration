using BugFree.Security;

using System.Text;
using System.Text.Json.Serialization;
using System.Xml.Serialization;

namespace BugFree.Configuration.Provider
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
        #region 属性
        /// <summary>提供者名称（类型名去掉后缀“ConfigProvider”）。</summary>
        public string? Name => GetType().Name.Replace(nameof(ConfigProvider), string.Empty);
        /// <summary>是否新建（当目标文件不存在或内容为空时为 true）。</summary>
        public bool IsNew { get; protected set; }
        /// <summary>配置特性（由 <see cref="Config{TConfig}"/> 初始化）。</summary>
        public ConfigAttribute Attribute { get; set; }
        /// <summary>默认 UTF-8 编码（无 BOM）。</summary>
        protected UTF8Encoding UTF8Encoding => new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        #endregion

        /// <summary>加载配置到模型。</summary>
        /// <typeparam name="T">模型类型。</typeparam>
        /// <param name="path">可选路径；为空则使用 <see cref="ConfigAttribute.GetFullPath"/>。</param>
        public T Load<T>(string? path = null) where T : new()
        {
            var filePath = path ?? Attribute.GetFullPath();
            if (!File.Exists(filePath)) { IsNew = true; return new T(); }
            try
            {
                // 安全读取
                using var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                using var reader = new StreamReader(fileStream, UTF8Encoding);
                var context = reader.ReadToEnd();
                if (string.IsNullOrWhiteSpace(context)) { IsNew = true; return new T(); }
                var plain = Decrypt(context);
                var model = Deserialize<T>(plain) ?? new T();
                return model;
            }
            catch (IOException ex)
            {
                if (ex.HResult == -2147024864)// 0x80070020 ERROR_SHARING_VIOLATION
                {
                    // 文件被锁定时等待重试
                    Thread.Sleep(100);
                    return Load<T>(path);  // 简单重试，实际应加入重试次数限制
                }
                throw;
            }
        }

        /// <summary> 保存模型实例。</summary>
        /// <typeparam name="T">模型类型。</typeparam>
        /// <param name="model">模型实例。</param>
        /// <param name="path">可选路径；为空则使用 <see cref="ConfigAttribute.GetFullPath"/>。</param>
        public bool Save<T>(T model, string? path = null)
        {
            var filePath = path ?? Attribute.GetFullPath();
            if (string.IsNullOrEmpty(filePath) || model == null) return false;

            // 跨进程安全写入：先写入临时文件，再原子替换目标文件，尽量避免读到半文件/被其他进程占用
            var tmp = Path.Combine(Path.GetTempPath(), $"{Path.GetFileName(filePath)}.{Guid.NewGuid():N}.tmp");
            try
            {
                var dir = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(dir)) { Directory.CreateDirectory(dir); }
                var plain = Serialize(model);
                var cipher = Encrypt(plain);
                File.WriteAllText(tmp, cipher, UTF8Encoding);
                // 安全替换目标文件
                if (File.Exists(filePath))
                {
                    var backup = filePath + ".bak";
                    File.Replace(tmp, filePath, backup);
                    File.Delete(backup);
                }
                else { File.Move(tmp, filePath); }
                IsNew = false;
                return true;
            }
            catch { throw; }
            finally { if (File.Exists(tmp)) File.Delete(tmp); }
        }

        /// <summary>释放资源。</summary>
        public virtual void Dispose()
        {
           
        }
        /// <summary>序列化模型为文本（由具体提供者实现）。</summary>
        protected abstract string Serialize<T>(T model);
        /// <summary>反序列化文本为模型（由具体提供者实现）。</summary>
        protected abstract T Deserialize<T>(string text) where T : new();
        /// <summary>加密明文（当 <see cref="ConfigAttribute.IsEncrypted"/> 为 true 时）。</summary>
        protected virtual string Encrypt(string plainText)
            => Attribute.IsEncrypted ? plainText.EncryptSymmetric(SymmetricAlgorithm.AesGcm, Attribute.Secret) : plainText;
        /// <summary>解密密文（当 <see cref="ConfigAttribute.IsEncrypted"/> 为 true 时）。</summary>
        protected virtual string Decrypt(string cipherText)
            => Attribute.IsEncrypted ? cipherText.DecryptSymmetric(Attribute.Secret) : cipherText;
        /// <summary>根据配置特性创建具体的配置提供者实例。</summary>
        public static ConfigProvider Create(ConfigAttribute config)
        {
            return config.Provider switch
            {
                ConfigProviderType.Ini => new IniConfigProvider(),
                ConfigProviderType.Xml => new XmlConfigProvider(),
                ConfigProviderType.Json => new JsonConfigProvider(),
                _ => throw new NotSupportedException($"不支持的配置提供者 {config.Provider}")
            };
        }
    }
}
