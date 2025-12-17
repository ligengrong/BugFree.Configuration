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
        
        static readonly ConcurrentDictionary<(Type type, ConfigProviderType? provider), String> _CommentHeaderCache = new();

        #region 属性
        /// <summary>提供者名称（类型名去掉后缀“ConfigProvider”）。</summary>
        public string? Name => GetType().Name.Replace(nameof(ConfigProvider), string.Empty);
        /// <summary>是否新建（当目标文件不存在或内容为空时为 true）。</summary>
        public bool IsNew { get; protected set; }
        /// <summary>配置特性（由 <see cref="Config{TConfig}"/> 初始化）。</summary>
        public ConfigAttribute Attribute { get; set; } = default!;
        /// <summary>默认 UTF-8 编码（无 BOM）。</summary>
        protected UTF8Encoding UTF8Encoding => new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        #endregion

        #region 监控/轮询（可选）
        /// <summary>文件监视器（可选）。</summary>
        protected FileSystemWatcher? FileWatcher { get; private set; }
        /// <summary>轮询定时器（文件系统事件不可用时兜底）。</summary>
        protected Timer? PollingTimer { get; private set; }
        /// <summary>去抖定时器（合并短时间内多次事件）。</summary>
        protected Timer? DebounceTimer { get; private set; }
        /// <summary>轮询间隔，默认 5s。</summary>
        protected TimeSpan PollingInterval { get; set; } = TimeSpan.FromSeconds(5);
        /// <summary>去抖窗口，默认 300ms。</summary>
        protected TimeSpan DebounceWindow { get; set; } = TimeSpan.FromMilliseconds(300);
        /// <summary>刷新前延迟，默认 150ms（降低写入冲突）。</summary>
        protected TimeSpan PreReloadDelay { get; set; } = TimeSpan.FromMilliseconds(150);
        /// <summary>上次写入时间（UTC）。</summary>
        protected DateTime LastWriteTimeUtc { get; set; } = DateTime.MinValue;
        /// <summary>内部同步对象。</summary>
        protected object WatchSync { get; } = new();
        /// <summary>是否已启用监控（幂等保护）。</summary>
        protected bool MonitoringStarted { get; private set; }
        /// <summary>变更后触发的回调（由上层提供）。</summary>
        protected Action? OnReload { get; private set; }

        /// <summary>
        /// 启动对配置文件的变更监控（FileSystemWatcher 优先，失败则回退为轮询）。
        /// 不改变现有加载/保存逻辑，仅在变更稳定后触发 onReload 回调。
        /// </summary>
        public virtual void StartReloading(Action onReload, string? path = null, TimeSpan? debounce = null, TimeSpan? pollingInterval = null, TimeSpan? preReloadDelay = null)
        {
            if (onReload is null) throw new ArgumentNullException(nameof(onReload));
            lock (WatchSync)
            {
                OnReload = onReload;
                if (debounce.HasValue) DebounceWindow = debounce.Value;
                if (pollingInterval.HasValue) PollingInterval = pollingInterval.Value;
                if (preReloadDelay.HasValue) PreReloadDelay = preReloadDelay.Value;
                if (MonitoringStarted) return;

                StartFileWatcher(path);
                if (FileWatcher is null) StartPollingTimer(path);
                MonitoringStarted = FileWatcher != null || PollingTimer != null;
            }
        }

        /// <summary>停止监控并释放相关资源。</summary>
        public virtual void StopReloading()
        {
            lock (WatchSync)
            {
                DebounceTimer?.Dispose();
                PollingTimer?.Dispose();
                FileWatcher?.Dispose();
                DebounceTimer = null;
                PollingTimer = null;
                FileWatcher = null;
                MonitoringStarted = false;
            }
        }

        /// <summary>启动文件监视器。</summary>
        protected virtual void StartFileWatcher(string? path = null)
        {
            if (FileWatcher != null) return;
            var filePath = path ?? Attribute.GetFullPath();
            try
            {
                var directory = Path.GetDirectoryName(filePath);
                var fileName = Path.GetFileName(filePath);
                if (string.IsNullOrEmpty(directory) || string.IsNullOrEmpty(fileName)) return;

                var fsw = new FileSystemWatcher(directory, fileName)
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                    EnableRaisingEvents = true,
                    IncludeSubdirectories = false
                };

                fsw.Changed += (_, __) => ScheduleDebounce();
                //fsw.Created += (_, __) => ScheduleDebounce();
                //fsw.Renamed += (_, __) => ScheduleDebounce();
                //fsw.Deleted += (_, __) => ScheduleDebounce();

                FileWatcher = fsw;
            }
            catch
            {
                FileWatcher?.Dispose();
                FileWatcher = null; // 失败时由轮询兜底
            }
        }

        /// <summary>启动轮询定时器（仅在未启用文件监视器时）。</summary>
        protected virtual void StartPollingTimer(string? path = null)
        {
            if (PollingTimer != null || FileWatcher != null) return;
            var filePath = path ?? Attribute.GetFullPath();
            try { LastWriteTimeUtc = File.Exists(filePath) ? File.GetLastWriteTimeUtc(filePath) : DateTime.MinValue; }
            catch { LastWriteTimeUtc = DateTime.MinValue; }

            PollingTimer = new Timer(_ =>
            {
                try
                {
                    var cur = File.Exists(filePath) ? File.GetLastWriteTimeUtc(filePath) : DateTime.MinValue;
                    if (LastWriteTimeUtc == DateTime.MinValue) { LastWriteTimeUtc = cur; return; }
                    if (cur != LastWriteTimeUtc)
                    {
                        LastWriteTimeUtc = cur;
                        ScheduleDebounce();
                    }
                }
                catch
                {
                    // 轮询异常时静默，下周期重试
                }
            }, null, PollingInterval, PollingInterval);
        }

        /// <summary>计划一次去抖后的刷新。</summary>
        protected void ScheduleDebounce()
        {
            lock (WatchSync)
            {
                DebounceTimer?.Dispose();
                DebounceTimer = new Timer(async _ => await TriggerReloadAsync().ConfigureAwait(false), null, DebounceWindow, Timeout.InfiniteTimeSpan);
            }
        }

        /// <summary>在去抖窗口后触发刷新。</summary>
        protected virtual async Task TriggerReloadAsync()
        {
            try
            {
                if (PreReloadDelay > TimeSpan.Zero)
                    await Task.Delay(PreReloadDelay).ConfigureAwait(false);
                OnReload?.Invoke();
            }
            catch
            {
                // 刷新回调异常不向外传播，避免影响宿主。
            }
        }
        #endregion

        /// <summary>加载配置到模型。</summary>
        /// <typeparam name="T">模型类型。</typeparam>
        /// <param name="path">可选路径；为空则使用 <see cref="ConfigAttribute.GetFullPath"/>。</param>
        public T Load<T>(string? path = null) where T : new()
        {
            var filePath = path ?? Attribute.GetFullPath();
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
                    if (string.IsNullOrWhiteSpace(context)) { IsNew = true; return new T(); }
                    var plain = Decrypt(context);
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

        /// <summary> 保存模型实例。</summary>
        /// <typeparam name="T">模型类型。</typeparam>
        /// <param name="model">模型实例。</param>
        /// <param name="path">可选路径；为空则使用 <see cref="ConfigAttribute.GetFullPath"/>。</param>
        public bool Save<T>(T model, string? path = null)
        {
            var filePath = path ?? Attribute.GetFullPath();
            if (string.IsNullOrEmpty(filePath) || model == null) return false;

            // 跨进程安全写入：先写入临时文件（同目录同卷），再原子替换目标文件，尽量避免读到半文件/被其他进程占用
            var tmp = String.Empty;
            try
            {
                var dir = Path.GetDirectoryName(filePath);
                if (!String.IsNullOrEmpty(dir)) { Directory.CreateDirectory(dir); }
                tmp = Path.Combine(dir ?? Path.GetTempPath(), $"{Path.GetFileName(filePath)}.{Guid.NewGuid():N}.tmp");
              
                var plain = Serialize(model);
                plain = InjectComments<T>(plain);
                var cipher = Encrypt(plain);
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
        public virtual void Dispose()
        {
            StopReloading();
        }
        /// <summary>序列化模型为文本（由具体提供者实现）。</summary>
        protected abstract String Serialize<T>(T model);
        /// <summary>反序列化文本为模型（由具体提供者实现）。</summary>
        protected abstract T Deserialize<T>(String text) where T : new();
      
        /// <summary>加密明文（当 <see cref="ConfigAttribute.IsEncrypted"/> 为 true 时）。</summary>
        protected virtual String Encrypt(String plainText)
            => Attribute.IsEncrypted ? plainText.EncryptSymmetric(SymmetricAlgorithm.AesGcm, Attribute.Secret!) : plainText;
        /// <summary>解密密文（当 <see cref="ConfigAttribute.IsEncrypted"/> 为 true 时）。</summary>
        protected virtual String Decrypt(String cipherText)
            => Attribute.IsEncrypted ? cipherText.DecryptSymmetric(Attribute.Secret!) : cipherText;

        protected virtual Boolean IsIgnored(PropertyInfo property)
        {
            if (property.GetCustomAttribute<XmlIgnoreAttribute>() != null) { return true; }
            if (property.GetCustomAttribute<IgnoreDataMemberAttribute>() != null) { return true; }
            if (property.GetCustomAttribute<JsonIgnoreAttribute>() != null) { return true; }
            if (property.GetCustomAttribute<YamlIgnoreAttribute>() != null) { return true; }
            return false;
        }
        String InjectComments<T>(String plain)
        {
            if (Attribute.IsEncrypted) { return plain; }
            if (String.IsNullOrEmpty(plain)) { return plain; }

            var header = _CommentHeaderCache.GetOrAdd((typeof(T), Attribute.Provider), (key) => {
                
                var properties = typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance);
                if (null == properties ||0 >= properties.Length) { return String.Empty; }
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
            if (Attribute.Provider == ConfigProviderType.Xml && plain.StartsWith("<?xml", StringComparison.Ordinal))
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
        public static ConfigProvider Create(ConfigAttribute config)
        {
            return config.Provider switch
            {
                ConfigProviderType.Ini => new IniConfigProvider(),
                ConfigProviderType.Xml => new XmlConfigProvider(),
                ConfigProviderType.Json => new JsonConfigProvider(),
                ConfigProviderType.Yaml => new YamlConfigProvider(),
                _ => throw new NotSupportedException($"不支持的配置提供者 {config.Provider}")
            };
        }
    }
}
