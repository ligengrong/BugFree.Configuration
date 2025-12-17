namespace BugFree.Configuration.HotReloader
{
    /// <summary>基于定时轮询的热重载器。</summary>
    /// <remarks>适用于不支持文件系统监视器的环境或需要更可控的轮询策略的场景。</remarks>
    internal sealed class TimerReloader : HotReloaderBase
    {
        /// <summary>定时器实例。</summary>
        Timer? _Timer;

        /// <summary>是否已启动。</summary>
        Int32 _isStarted;

        /// <summary>是否正在执行轮询（用于防止重入）。</summary>
        Int32 _isTicking;

        /// <summary>首次触发延迟（毫秒）。</summary>
        readonly Int32 _dueTime;

        /// <summary>轮询周期（毫秒）。</summary>
        readonly Int32 _period;

        /// <summary>创建定时轮询热重载器。</summary>
        /// <param name="filePath">配置文件完整路径。</param>
        /// <param name="dueTime">首次触发延迟（毫秒）。</param>
        /// <param name="period">轮询周期（毫秒）。</param>
        public TimerReloader(String filePath, Int32 dueTime = 5 * 1_000, Int32 period = 5 * 1_000) : base(filePath)
        {
            _dueTime = dueTime;
            _period = period;
        }

        /// <summary>启动定时轮询。</summary>
        public override void Start()
        {
            if (!File.Exists(FilePath)) { throw new FileNotFoundException(FilePath); }
            LastWriteTimeUtc = File.GetLastWriteTimeUtc(FilePath);
            if (1 == Interlocked.Exchange(ref _isStarted, 1)) { return; }
            _Timer = new Timer(OnTimer, null, _dueTime, _period);
        }

        /// <summary>停止定时轮询。</summary>
        public override void Stop()
        {
            Interlocked.Exchange(ref _isStarted, 0);
            var timer = Interlocked.Exchange(ref _Timer, null);
            timer?.Dispose();
        }

        /// <summary>定时器回调。</summary>
        /// <param name="state">状态对象。</param>
        void OnTimer(Object? state)
        {
            if (0 == Volatile.Read(ref _isStarted)) { return; }
            if (1 == Interlocked.Exchange(ref _isTicking, 1)) { return; }
            try { Reload(); }
            finally { Interlocked.Exchange(ref _isTicking, 0); }
        }
    }
}
