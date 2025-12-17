using BugFree.Configuration.HotReloader;

using System.Collections.Concurrent;

namespace BugFree.Configuration
{
    /// <summary>通用配置存储：不要求模型继承 <see cref="Config{TConfig}"/>，支持缓存与热重载。</summary>
    /// <remarks>
    /// 说明：
    /// - 本扩展主要缓存 <see cref="ConfigAttribute"/> 与热重载器实例（按模型类型区分）；
    /// - 不维护全局单例配置对象；调用方可自行持有返回的配置实例；
    /// - 若需要“变更时自动替换当前配置实例”的语义，优先使用 <see cref="Config{TConfig}.Current"/> 模式。
    /// </remarks>
    public static class ConfigExtensions
    {
        /// <summary>配置特性缓存（按模型类型缓存）。</summary>
        static readonly ConcurrentDictionary<Type, ConfigAttribute> _CacheAttributes = new();

        /// <summary>热重载器缓存（按模型类型缓存）。</summary>
        static readonly ConcurrentDictionary<Type, HotReloaderBase> _CacheReloaders = new();

        /// <summary>获取配置实例（带缓存、可选自动保存、可选热重载）。</summary>
        /// <typeparam name="T">配置模型类型（需公共无参构造）。</typeparam>
        /// <param name="attribute">配置特性（用于创建 Provider、默认路径与加密等）。</param>
        /// <returns>配置实例。</returns>
        /// <remarks>
        /// 行为约定：
        /// - 首次加载时，若目标文件不存在，会先保存一份默认配置文件（便于用户编辑）；
        /// - 当启用热重载时，仅会触发重新读取文件（不自动替换调用方持有的引用）。
        /// </remarks>
        /// <example>
        /// <code>
        /// var attr = new ConfigAttribute("app", ConfigProviderType.Json);
        /// var cfg = new AppConfig().Load(attr);
        /// </code>
        /// </example>
        public static T Load<T>(this T model, ConfigAttribute attribute) where T : class, new()
        {
            if (attribute is null) { throw new ArgumentNullException(nameof(attribute)); }
            var provider = ConfigProvider.Create(attribute.Provider);
            _CacheAttributes.GetOrAdd(typeof(T), attribute);
            var path = attribute.GetFullPath();
            if (!File.Exists(path)) { Save(model); }
            _CacheReloaders.GetOrAdd(typeof(T), _ =>
            {
                HotReloaderBase? reloader = attribute.Reloader switch
                {
                    HotReloaderType.FileWatcher => new FileWatcherReloader(path),
                    HotReloaderType.Timer => new TimerReloader(path),
                    _ => null,
                };

                if (reloader is not null)
                {
                    reloader.OnReload = () =>
                    {
                        lock (typeof(T))
                        {
                            // 这里注意：不要递归走缓存逻辑，否则有可能死循环 / 重复初始化
                            var providerInner = ConfigProvider.Create(attribute.Provider);
                            providerInner.Load<T>(attribute);
                        }
                    };
                    reloader?.Start();
                }
                return reloader;
            });
            model = provider.Load<T>(attribute);
            return model;
        }

        /// <summary>保存配置实例。</summary>
        /// <typeparam name="T">配置模型类型（需公共无参构造）。</typeparam>
        /// <param name="model">配置实例。为空时创建默认实例保存。</param>
        /// <param name="path">可选保存路径；为空则使用缓存的 <see cref="ConfigAttribute"/> 计算路径。</param>
        /// <returns>是否保存成功。</returns>
        /// <remarks>
        /// 注意：当传入 <paramref name="path"/> 且与当前模型类型对应的热重载监视文件不一致时，本方法不会尝试触发热重载器的“自写入抑制”标记。
        /// </remarks>
        public static Boolean Save<T>(this T model, String? path = null) where T : class, new()
        {
            model ??= new T();
            if (!_CacheAttributes.TryGetValue(typeof(T), out var attr)) { throw new ArgumentNullException(nameof(attr)); }
            var provider = ConfigProvider.Create(attr.Provider);
            _CacheReloaders.TryGetValue(typeof(T), out var reloader);
            reloader?.MarkFileChanged();
            var success = provider.Save(model, attr, path);
            reloader?.MarkFileChanged();
            return success;
        }
    }
}