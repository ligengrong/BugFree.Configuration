namespace BugFree.Configuration.HotReloader
{
    /// <summary>基于 <see cref="FileSystemWatcher"/> 的热重载器。</summary>
    internal sealed class FileWatcherReloader : HotReloaderBase
    {
        /// <summary>文件系统监视器实例。</summary>
        FileSystemWatcher? _FileWatcher;

        /// <summary>创建文件监视热重载器。</summary>
        /// <param name="filePath">配置文件完整路径。</param>
        public FileWatcherReloader(String filePath) : base(filePath) { }

        /// <summary>启动文件监视。</summary>
        public override void Start()
        {
            if (!File.Exists(FilePath)) { throw new FileNotFoundException(FilePath); }
            LastWriteTimeUtc = File.GetLastWriteTimeUtc(FilePath);

            if (null == _FileWatcher)
            {
                var directory = Path.GetDirectoryName(FilePath);
                var fileName = Path.GetFileName(FilePath);
                if (String.IsNullOrEmpty(directory)) { directory = Directory.GetCurrentDirectory(); }
                if (String.IsNullOrEmpty(fileName)) { throw new ArgumentException("无效的文件路径", nameof(FilePath)); }
                _FileWatcher = new FileSystemWatcher(directory, fileName)
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                    EnableRaisingEvents = true,
                    IncludeSubdirectories = false
                };

                _FileWatcher.Changed += (_, __) => Reload();
                //_FileWatcher.Created += (_, __) => Reload();
                //_FileWatcher.Renamed += (_, __) => Reload();
            }
            else { _FileWatcher.EnableRaisingEvents = true; }
        }

        /// <summary>停止文件监视。</summary>
        public override void Stop()
        {
            if (null == _FileWatcher) { return; }
            _FileWatcher.EnableRaisingEvents = false;
        }
    }
}
