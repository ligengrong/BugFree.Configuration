using BugFree.Configuration.HotReloader;

using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.Serialization;
using System.Text.Json.Serialization;
using System.Xml.Serialization;

using YamlDotNet.Serialization;

namespace BugFree.Configuration
{
    /// <summary>配置文件基类</summary>
    /// <remarks>
    /// 标准用法：TConfig.Current
    /// 配置实体类通过<see cref="ConfigAttribute"/>特性指定配置文件路径。
    /// Current将加载配置文件，如果文件不存在或者加载失败，将实例化一个对象返回。
    /// </remarks>
    /// <typeparam name="TConfig">配置模型类型（必须带 <see cref="ConfigAttribute"/> 特性，且具备公共无参构造）。</typeparam>
    public abstract class Config<TConfig> : IDisposable where TConfig : Config<TConfig>, new()
    {
        #region 静态属性&函数
        /// <summary>热重载器</summary>
        static HotReloaderBase? _Reloader;
        /// <summary>当前使用的提供者</summary>
        static ConfigProvider Provider { get; set; }
        /// <summary>当前配置实例</summary>
        static TConfig? _Current;

        /// <summary>当前配置特性（由 <typeparamref name="TConfig"/> 上的 <see cref="ConfigAttribute"/> 提供）。</summary>
        static ConfigAttribute _ConfigAttribute;
        /// <summary>当前配置实例</summary>
        public static TConfig Current
        {
            get
            {
                if (_Current != null) { return _Current; }
                lock (typeof(TConfig))
                {
                    if (_Current != null) { return _Current; }
                    var prv = Provider ?? throw new InvalidOperationException("配置提供者未初始化。");
                    var config = prv.Load<TConfig>(_ConfigAttribute);
                    config.OnLoaded();
                    if (config.IsNew) { config.Save(); }
                    //启动文件监视器以支持热重载
                    _Reloader = _ConfigAttribute.Reloader switch
                    {
                        HotReloaderType.FileWatcher => new FileWatcherReloader(_ConfigAttribute.GetFullPath()),
                        HotReloaderType.Timer => new TimerReloader(_ConfigAttribute.GetFullPath()),
                        _ => null,
                    };
                    if (null != _Reloader)
                    {
                        _Reloader.OnReload ??= () =>
                        {
                            lock (typeof(TConfig))
                            {
                                var reloaded = prv.Load<TConfig>(_ConfigAttribute);
                                reloaded.OnLoaded();
                                _Current = reloaded;
                            }
                        };
                        _Reloader?.Start();
                    }
                    _Current = config;
                }

                return _Current!;
            }
            set { _Current = value; }
        }

        /// <summary>静态构造函数：解析 <see cref="ConfigAttribute"/> 并创建 <see cref="ConfigProvider"/>。</summary>
        static Config()
        {
            // 创建提供者
            _ConfigAttribute = typeof(TConfig).GetCustomAttribute<ConfigAttribute>(true)
                ?? throw new InvalidOperationException($"配置类 {typeof(TConfig).FullName} 必须使用 {nameof(ConfigAttribute)} 特性进行标记。");
            _ConfigAttribute.Name ??= nameof(TConfig);
            Provider = ConfigProvider.Create(_ConfigAttribute.Provider);
        }

        #endregion
        /// <summary>是否新的配置文件</summary>
        [XmlIgnore, IgnoreDataMember, JsonIgnore, YamlIgnore]
        public Boolean IsNew => Provider?.IsNew ?? false;
        #region 成员方法
        /// <summary>从配置文件中读取完成后触发</summary>
        protected virtual void OnLoaded() { }
        /// <summary>保存到配置文件中去</summary>
        public virtual void Save()
        {
            // 防止 Save 引发热重载自触发：保存前后各标记一次
            _Reloader?.MarkFileChanged();
            Provider?.Save((TConfig)this, _ConfigAttribute);
            _Reloader?.MarkFileChanged();
        }
        /// <summary>
        /// 释放资源（基类默认无托管资源可释放）。
        /// </summary>
        public void Dispose() { }
        #endregion
    }


    /// <summary>通用配置存储（支持缓存与热重载）</summary>
    /// <remarks>
    /// 与 <see cref="Config{TConfig}"/> 的差异：本类不要求配置模型继承任何基类，仅依赖 <see cref="ConfigAttribute"/> 描述来源。
    /// 
    /// 关于泛型类型 <typeparamref name="T"/> 的限制：
    /// 1) 代码层面限制：仅支持引用类型且必须具备公共无参构造（<c>where T : class, new()</c>）。
    /// 2) 序列化/反序列化限制：模型必须能被所选 <see cref="ConfigProvider"/> 正常序列化与反序列化（通常要求公共属性可读写）。
    /// 3) 热重载/保存写回限制：为保持“外部引用不变”，仅写回公共实例、可读可写、非索引器属性；字段、只读属性、索引器、非公共属性不会被写回。
    /// 4) 写回为浅拷贝：属性值直接赋值；若属性值是引用类型对象，将整体替换该引用（不会深拷贝对象图）。
    /// </remarks>
    public class Config
    {
        static readonly Config _Instance = new();
        /// <summary>单例实例。</summary>
        public static Config Instance => _Instance;
        /// <summary>配置特性缓存（按模型类型缓存）。</summary>
        readonly ConcurrentDictionary<Type, ConfigAttribute> _CacheAttributes = new();
        /// <summary>热重载器缓存（按模型类型缓存）。</summary>
        readonly ConcurrentDictionary<Type, HotReloaderBase> _CacheReloaders = new();
        /// <summary>配置实例缓存（按模型类型缓存，确保同一类型返回同一引用）。</summary>
        readonly ConcurrentDictionary<Type, Object> _CacheConfig = new();
        /// <summary>可写属性缓存（按模型类型缓存，减少反射开销）。</summary>
        readonly ConcurrentDictionary<Type, PropertyInfo[]> _CacheProperties = new();
        /// <summary>私有构造函数（单例）。</summary>
        Config() { }

        /// <summary>加载配置</summary>
        /// <remarks>
        /// <typeparamref name="T"/> 必须满足：引用类型 + 公共无参构造（<c>where T : class, new()</c>）。
        /// 
        /// 语义说明：
        /// - 同一 <typeparamref name="T"/> 始终返回同一引用对象；热重载时通过“属性写回”更新该引用对象内容。
        /// - 首次加载若配置文件不存在，会先创建默认配置文件再加载。
        /// </remarks>
        /// <typeparam name="T">配置模型类型。</typeparam>
        /// <param name="attribute">配置特性，指定文件路径、提供者与热重载方式。</param>
        /// <returns>配置实例（同一类型会始终返回同一引用对象）。</returns>
        public virtual T Load<T>(ConfigAttribute attribute) where T : class, new()
        {
            if (attribute is null) { throw new ArgumentNullException(nameof(attribute)); }
            var type = typeof(T);

            // 1) 缓存配置特性（供 Save/Current 使用）
            _CacheAttributes.AddOrUpdate(type, attribute, (_, __) => attribute);

            // 2) 同一类型只允许初始化一次，避免并发下创建多个实例导致“引用不一致”
            lock (type)
            {
                // 2.1) 若已有缓存实例，直接返回该引用
                if (_CacheConfig.TryGetValue(type, out var value) && value is T v) { return v; }

                // 2.2) 创建配置提供者与文件路径
                var provider = ConfigProvider.Create(attribute.Provider);
                var path = attribute.GetFullPath();

                // 2.3) 配置文件不存在时，先创建默认文件，保证后续可加载
                if (!File.Exists(path))
                {
                    // Save<T>() 依赖 _CacheAttributes，因此必须在缓存 attribute 之后调用
                    Save<T>();
                }

                // 2.4) 创建（或复用）热重载器：重载时把“新内容”写回“旧引用对象”
                _CacheReloaders.GetOrAdd(type, _ => CreateReloader<T>(attribute, path));

                // 2.5) 首次加载：读取配置并缓存实例引用（后续 Load/Current 直接返回该引用）
                var model = provider.Load<T>(attribute);
                _CacheConfig[type] = model;
                return model;
            }
        }

        /// <summary>保存配置</summary>
        /// <remarks>
        /// <typeparamref name="T"/> 必须满足：引用类型 + 公共无参构造（<c>where T : class, new()</c>）。
        /// 
        /// 保存成功后会将 <paramref name="model"/> 的内容写回缓存实例，以保证外部持有的配置对象引用不变。
        /// 写回规则见类注释（仅公共实例可读可写非索引器属性）。
        /// </remarks>
        /// <typeparam name="T">配置模型类型。</typeparam>
        /// <param name="model">配置实例，为空则创建默认实例并保存。</param>
        /// <returns>是否保存成功。</returns>
        public virtual Boolean Save<T>(T? model = null) where T : class, new()
        {
            // 1) 若未传入实例，则创建默认实例
            model ??= new T();

            // 2) Save 依赖已缓存的 ConfigAttribute（由 Load 写入）
            if (!_CacheAttributes.TryGetValue(typeof(T), out var attr)) { throw new InvalidOperationException($"未找到类型 {typeof(T).FullName} 的配置特性缓存，请先调用 Load<T>(ConfigAttribute)。"); }

            // 3) 创建提供者，并获取（可选的）热重载器
            var provider = ConfigProvider.Create(attr.Provider);
            _CacheReloaders.TryGetValue(typeof(T), out var reloader);

            // 4) 防止 Save 引发热重载自触发：保存前后各标记一次
            reloader?.MarkFileChanged();
            provider.Save(model, attr);

            // 5) 保存后把内容写回缓存实例（保持外部引用不变）
            UpdateProperties(model);
            reloader?.MarkFileChanged();
            return true;
        }
        /// <summary>获取当前配置实例</summary>
        /// <remarks>
        /// - 若该类型已经通过 <see cref="Load{T}(ConfigAttribute)"/> 加载过，则直接返回缓存引用。
        /// - 若未加载但存在缓存的 <see cref="ConfigAttribute"/>，则触发一次加载并返回。
        /// - <typeparamref name="T"/> 必须满足：引用类型 + 公共无参构造（<c>where T : class, new()</c>）。
        /// </remarks>
        /// <typeparam name="T">配置模型类型。</typeparam>
        /// <returns>当前配置实例（同一类型同一引用）。</returns>
        public virtual T Current<T>() where T : class, new()
        {
            var type = typeof(T);

            // 1) 若已有缓存实例，直接返回
            if (_CacheConfig.TryGetValue(type, out var cached) && cached is T cachedTyped) { return cachedTyped; }

            // 2) 若已有缓存 attribute，则触发一次 Load 并返回
            if (_CacheAttributes.TryGetValue(type, out var attribute)) { return Load<T>(attribute); }

            // 3) 无实例也无 attribute：无法确定配置来源
            throw new InvalidOperationException($"未找到类型 {type.FullName} 的缓存实例与配置特性，请先调用 Load<T>(ConfigAttribute)。");
        }
        /// <summary>创建并启动热重载器</summary>
        /// <remarks>
        /// 热重载触发时会重新加载一个“新模型”，然后将其属性值写回缓存实例（不替换引用）。
        /// </remarks>
        /// <typeparam name="T">配置模型类型。</typeparam>
        /// <param name="attribute">配置特性。</param>
        /// <param name="path">配置文件完整路径。</param>
        /// <returns>热重载器实例。</returns>
        HotReloaderBase CreateReloader<T>(ConfigAttribute attribute, String path) where T : class, new()
        {
            var type = typeof(T);

            // 1) 当前仅支持 FileWatcher / Timer，默认回退 FileWatcher
            HotReloaderBase reloader = attribute.Reloader switch
            {
                HotReloaderType.Timer => new TimerReloader(path),
                _ => new FileWatcherReloader(path),
            };

            // 2) 重载回调：加载“新模型”，并将其属性写回“旧引用对象”（不替换引用）
            reloader.OnReload = () =>
            {
                lock (type)
                {
                    // 2.1) 注意：不要递归走 Load/Current 等缓存逻辑，否则可能死循环/重复初始化
                    var provider = ConfigProvider.Create(attribute.Provider);
                    var model = provider.Load<T>(attribute);

                    // 2.2) 将新内容写回缓存实例（保持外部引用不变）
                    UpdateProperties(model);
                }
            };

            // 3) 启动热重载
            reloader.Start();
            return reloader;
        }
        /// <summary>将新加载的配置内容写回缓存实例</summary>
        /// <remarks>
        /// 写回规则：仅复制公共实例、可读可写、非索引器属性；字段、只读属性、索引器、非公共属性不会被处理。
        /// 写回方式：浅拷贝（属性值直接赋值）。
        /// </remarks>
        /// <typeparam name="T">对象类型。</typeparam>
        /// <param name="source">源对象（读取属性）。</param>
        void UpdateProperties<T>(T source) where T : class, new()
        {
            var type = typeof(T);

            // 1) 已有缓存实例：把 source 的属性拷贝到 target，保持外部引用不变
            if (_CacheConfig.TryGetValue(type, out var value) && value is T target)
            {
                // 1.1) 同一引用无需拷贝（常见于 Save 传入缓存实例）
                if (ReferenceEquals(source, target)) { return; }

                // 1.2) 缓存可读可写且非索引器的公共实例属性，减少反射开销
                var properties = _CacheProperties.GetOrAdd(type, t =>
                   t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                    .Where(p => p.CanRead && p.CanWrite && p.GetIndexParameters().Length == 0)
                    .ToArray());

                // 1.3) 逐属性赋值（将新配置写回旧对象）
                foreach (var prop in properties)
                {
                    var val = prop.GetValue(source);
                    prop.SetValue(target, val);
                }

                // 注意：此处不能用 source 覆盖缓存引用，否则会破坏“外部引用不变”的语义。
                return;
            }

            // 2) 没有缓存实例：直接缓存该引用（首次 Load 或首次 Reload）
            _CacheConfig[type] = source;
        }
    }
}
