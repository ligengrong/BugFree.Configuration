//namespace BugFree.Configuration.Provider
//{
//    internal class FileWatcherProvider : IDisposable
//    {
//        /// <summary>文件监视器</summary>
//        protected FileSystemWatcher? FileWatcher { get; set; }
//        /// <summary>轮询定时器</summary>
//        protected Timer? PollingTimer { get; set; }
//        /// <summary>轮询间隔（当文件监视器不可用时使用）</summary>
//        protected TimeSpan PollingInterval { get; set; } = TimeSpan.FromSeconds(5);
//        /// <summary>最后修改时间</summary>
//        protected DateTime LastWriteTime { get; set; } = DateTime.MinValue;
//        /// <summary>配置重载事件</summary>
//        Action? OnConfigReloaded = null;

//        public void StartReloading(Action? onConfigReloaded, string? path = null)
//        {
//            OnConfigReloaded ??= onConfigReloaded;
//            StartFileWatcher(path);// 优先使用文件监视器
//            StartPollingTimer(path);// 监视器不可用时使用轮询
//        }
//        /// <summary>启动文件监视器</summary>
//        void StartFileWatcher(string? path = null)
//        {
//            if (FileWatcher != null) { return; }
//            var filePath = path ?? Attribute.GetFullPath();
//            try
//            {
//                var directory = Path.GetDirectoryName(filePath);
//                var fileName = Path.GetFileName(filePath);
//                if (!string.IsNullOrEmpty(directory) && !string.IsNullOrEmpty(fileName))
//                {
//                    FileWatcher = new FileSystemWatcher(directory, fileName)
//                    {
//                        NotifyFilter = NotifyFilters.LastWrite,
//                        EnableRaisingEvents = true,
//                        IncludeSubdirectories = false
//                    };
//                    //配置文件变化事件处理
//                    FileWatcher.Changed += (s, e) =>
//                    {
//                        // 延迟执行，确保文件写入完成
//                        Task.Delay(100).ContinueWith(_ => OnConfigReloaded?.Invoke());
//                    };
//                    //FileWatcher.Deleted += (s, e) => OnConfigReloaded?.Invoke();
//                }
//            }
//            catch (Exception)
//            {
//                // 文件监视器创建失败时静默处理
//                FileWatcher?.Dispose();
//                FileWatcher = null;
//            }
//        }
//        void StartPollingTimer(string? path = null)
//        {
//            if (PollingTimer != null || FileWatcher != null) { return; }
//            RefreshFileInfo(path);
//            // 文件监视器创建失败时使用轮询
//            PollingTimer = new Timer(_ => RefreshFileInfo(path), null, PollingInterval, PollingInterval);
//        }
//        void RefreshFileInfo(string? path)
//        {
//            var filePath = path ?? Attribute.GetFullPath();
//            if (!File.Exists(filePath)) { return; }
//            var lastWriteTime = File.GetLastWriteTimeUtc(filePath);
//            if (LastWriteTime == DateTime.MinValue) { LastWriteTime = lastWriteTime; return; }
//            if (lastWriteTime <= LastWriteTime) { return; }
//            LastWriteTime = lastWriteTime;
//            OnConfigReloaded?.Invoke();
//        }
//        public void Dispose()
//        {
//            FileWatcher?.Dispose();
//            PollingTimer?.Dispose();
//            PollingTimer = null;
//            FileWatcher = null;
//        }
//    }
//}
