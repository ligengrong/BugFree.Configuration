namespace BugFree.Configuration.HotReloader
{
    /// <summary>热重载器基类</summary>
    /// <remarks>
    /// 负责维护：
    /// - 监视文件路径（<see cref="FilePath"/>）
    /// - 最后写入时间基线（<see cref="LastWriteTimeUtc"/>）
    /// - Save 自触发抑制窗口（<see cref="MarkFileChanged"/>）
    /// 子类只需要实现 Start/Stop，并在检测到变更时调用 <see cref="Reload"/>。
    /// </remarks>
    internal abstract class HotReloaderBase : IDisposable
    {
        /// <summary>最后一次写入时间（UTC）。</summary>
        public DateTime? LastWriteTimeUtc { get; protected set; }

        /// <summary>监视的文件完整路径。</summary>
        public String FilePath { get; protected set; }

        /// <summary>当配置文件发生变化时触发。</summary>
        public Action? OnReload;

        /// <summary>抑制窗口截止时间（UTC ticks）。</summary>
        Int64 _suppressUntilUtcTicks;

        /// <summary>创建热重载器。</summary>
        /// <param name="filePath">配置文件完整路径。</param>
        protected HotReloaderBase(String filePath)
        {
            if (String.IsNullOrWhiteSpace(filePath)) { throw new ArgumentNullException(nameof(filePath)); }
            if (!File.Exists(filePath)) { throw new FileNotFoundException(filePath); }
            FilePath = filePath;
        }

        /// <summary>启动监视。</summary>
        public abstract void Start();

        /// <summary>停止监视。</summary>
        public abstract void Stop();

        /// <summary>
        /// 标记“本进程刚完成写入”，用于抑制 Save 引发的自触发，并更新写入时间基线。
        /// </summary>
        /// <remarks>
        /// 建议在保存前后各调用一次：
        /// - 保存前：进入抑制窗口；
        /// - 保存后：更新基线到最终写入时间。
        /// </remarks>
        public void MarkFileChanged()
        {
            // 抑制窗口：覆盖“写临时文件 + Move 覆盖”的短时间多次变化
            Interlocked.Exchange(ref _suppressUntilUtcTicks, DateTime.UtcNow.AddMilliseconds(800).Ticks);
            if (File.Exists(FilePath)) { LastWriteTimeUtc = File.GetLastWriteTimeUtc(FilePath); }
        }

        /// <summary>触发一次重载检查（由子类在检测到变更时调用）。</summary>
        protected void Reload()
        {
            // 检查是否在抑制窗口内/在抑制窗口内：认为是本进程 Save 导致，忽略
            if (DateTime.UtcNow.Ticks < Interlocked.Read(ref _suppressUntilUtcTicks)) { return; }
            if (!File.Exists(FilePath)) { return; }

            var lastWriteTimeUtc = File.GetLastWriteTimeUtc(FilePath);
            if (LastWriteTimeUtc == null || lastWriteTimeUtc > LastWriteTimeUtc)
            {
                LastWriteTimeUtc = lastWriteTimeUtc;
                OnReload?.Invoke();
            }
        }

        /// <summary>释放资源并停止监视。</summary>
        public void Dispose()
        {
            try { Stop(); }
            catch { }
        }
    }

    /// <summary>热重载器类型。</summary>
    public enum HotReloaderType
    {
        /// <summary>文件系统监视器。</summary>
        FileWatcher,

        /// <summary>定时轮询。</summary>
        Timer
    }
}
