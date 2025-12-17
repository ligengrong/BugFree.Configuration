using BugFree.Configuration.HotReloader;

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
            _ConfigAttribute = typeof(TConfig).GetCustomAttribute<ConfigAttribute>(true);
            if (null == _ConfigAttribute) { throw new InvalidOperationException($"配置类 {typeof(TConfig).FullName} 必须使用 {nameof(ConfigAttribute)} 特性进行标记。"); }
            _ConfigAttribute.Name ??= nameof(TConfig);
            Provider = ConfigProvider.Create(_ConfigAttribute?.Provider);
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
}
