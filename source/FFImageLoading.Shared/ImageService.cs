using System.Collections.Generic;
using System;
using FFImageLoading.Config;
using FFImageLoading.Work;
using System.Net.Http;
using FFImageLoading.Helpers;
using FFImageLoading.Cache;
using System.Threading;
using System.IO;
using System.Threading.Tasks;
using System.Linq;


#if SILVERLIGHT
using FFImageLoading.Concurrency;
#else
using System.Collections.Concurrent;
#endif

namespace FFImageLoading
{
    public class ImageService: IImageService
    {
        private volatile bool _initialized;
		private object _initializeLock = new object();
		private readonly MD5Helper _md5Helper = new MD5Helper();
		private readonly ConcurrentDictionary<string, string> _fullKeyToKey = new ConcurrentDictionary<string, string>();
		private Configuration _config;

		private static Lazy<ImageService> LazyInstance = new Lazy<ImageService>(() => new ImageService());
		public static IImageService Instance { get { return LazyInstance.Value; } }

		private ImageService() { }

        /// <summary>
        /// Gets FFImageLoading configuration
        /// </summary>
        /// <value>The configuration used by FFImageLoading.</value>
        public Configuration Config
		{
			get
			{
				InitializeIfNeeded();
				return _config;
			}
			private set
			{
				_config = value;
			}
		}

		/// <summary>
		/// Initializes FFImageLoading with given Configuration. It allows to configure and override most of it.
		/// </summary>
		/// <param name="configuration">Configuration.</param>
		public void Initialize(Configuration configuration)
		{
			lock (_initializeLock)
			{
				_initialized = false;

				if (Config != null)
				{
					// If DownloadCache is not updated but HttpClient is then we inform DownloadCache
					if (configuration.HttpClient != null && configuration.DownloadCache == null)
					{
						configuration.DownloadCache = Config.DownloadCache;
						configuration.DownloadCache.DownloadHttpClient = configuration.HttpClient;
					}

					// Redefine these if they were provided only
					configuration.HttpClient = configuration.HttpClient ?? Config.HttpClient;
					configuration.Scheduler = configuration.Scheduler ?? Config.Scheduler;
					configuration.Logger = configuration.Logger ?? Config.Logger;
					configuration.DownloadCache = configuration.DownloadCache ?? Config.DownloadCache;
					configuration.LoadWithTransparencyChannel = configuration.LoadWithTransparencyChannel;
					configuration.FadeAnimationEnabled = configuration.FadeAnimationEnabled;
					configuration.TransformPlaceholders = configuration.TransformPlaceholders;
					configuration.DownsampleInterpolationMode = configuration.DownsampleInterpolationMode;

					// Skip configuration for maxCacheSize and diskCache. They cannot be redefined.
					if (configuration.Logger != null)
						configuration.Logger.Debug("Skip configuration for maxCacheSize and diskCache. They cannot be redefined.");
					configuration.MaxCacheSize = Config.MaxCacheSize;
					configuration.DiskCache = Config.DiskCache;
				}


				InitializeIfNeeded(configuration);
			}
		}

		private void InitializeIfNeeded(Configuration userDefinedConfig = null)
        {
			if (_initialized)
				return;

			lock (_initializeLock)
			{
				if (_initialized)
					return;

				if (userDefinedConfig == null)
					userDefinedConfig = new Configuration();

				var httpClient = userDefinedConfig.HttpClient ?? new HttpClient();

				if (userDefinedConfig.HttpReadTimeout > 0)
				{
					httpClient.Timeout = TimeSpan.FromSeconds(userDefinedConfig.HttpReadTimeout);
				}

				var logger = userDefinedConfig.Logger ?? new MiniLogger();
				var scheduler = userDefinedConfig.Scheduler ?? new WorkScheduler(logger);
				var diskCache = userDefinedConfig.DiskCache ?? SimpleDiskCache.CreateCache("FFSimpleDiskCache");
				var downloadCache = userDefinedConfig.DownloadCache ?? new DownloadCache(httpClient, diskCache);

				userDefinedConfig.HttpClient = httpClient;
				userDefinedConfig.Scheduler = scheduler;
				userDefinedConfig.Logger = logger;
				userDefinedConfig.DiskCache = diskCache;
				userDefinedConfig.DownloadCache = downloadCache;

				Config = userDefinedConfig;

				_initialized = true;
			}
        }

        private IWorkScheduler Scheduler
        {
            get {
                InitializeIfNeeded();
                return Config.Scheduler;
            }
        }

        /// <summary>
        /// Constructs a new TaskParameter to load an image from a file.
        /// </summary>
        /// <returns>The new TaskParameter.</returns>
        /// <param name="filepath">Path to the file.</param>
        public TaskParameter LoadFile(string filepath)
        {
            InitializeIfNeeded();
            return TaskParameter.FromFile(filepath);
        }

        /// <summary>
        /// Constructs a new TaskParameter to load an image from a URL.
        /// </summary>
        /// <returns>The new TaskParameter.</returns>
        /// <param name="url">URL to the file</param>
        /// <param name="cacheDuration">How long the file will be cached on disk</param>
        public TaskParameter LoadUrl(string url, TimeSpan? cacheDuration = null)
        {
            InitializeIfNeeded();
            return TaskParameter.FromUrl(url, cacheDuration);
        }

		/// <summary>
		/// Constructs a new TaskParameter to load an image from a file from application bundle.
		/// </summary>
		/// <returns>The new TaskParameter.</returns>
		/// <param name="filepath">Path to the file.</param>
		public TaskParameter LoadFileFromApplicationBundle(string filepath)
		{
			InitializeIfNeeded();
			return TaskParameter.FromApplicationBundle(filepath);
		}

		/// <summary>
		/// Constructs a new TaskParameter to load an image from a compiled drawable resource.
		/// </summary>
		/// <returns>The new TaskParameter.</returns>
		/// <param name="resourceName">Name of the resource in drawable folder without extension</param>
		public TaskParameter LoadCompiledResource(string resourceName)
		{
			InitializeIfNeeded();
			return TaskParameter.FromCompiledResource(resourceName);
		}

		/// <summary>
		/// Constructs a new TaskParameter to load an image from a Stream.
		/// </summary>
		/// <returns>The new TaskParameter.</returns>
		/// <param name="resourceName">A function that allows a CancellationToken and returns the Stream to use. This function will be invoked by LoadStream().</param>
		public TaskParameter LoadStream(Func<CancellationToken, Task<Stream>> stream)
		{
			InitializeIfNeeded();
			return TaskParameter.FromStream(stream);
		}

        /// <summary>
        /// Gets a value indicating whether ImageService will exit tasks earlier
        /// </summary>
        /// <value><c>true</c> if it should exit tasks early; otherwise, <c>false</c>.</value>
        public bool ExitTasksEarly
        {
            get
            {
                return Scheduler.ExitTasksEarly;
            }
        }

        /// <summary>
        /// Sets a value indicating whether ImageService will exit tasks earlier
        /// </summary>
        /// <param name="exitTasksEarly">If set to <c>true</c> exit tasks early.</param>
        public void SetExitTasksEarly(bool exitTasksEarly)
        {
            Scheduler.SetExitTasksEarly(exitTasksEarly);
        }

        /// <summary>
        /// Sets a value indicating if all loading work should be paused (silently canceled).
        /// </summary>
        /// <param name="pauseWork">If set to <c>true</c> pause/cancel work.</param>
        public void SetPauseWork(bool pauseWork)
        {
            Scheduler.SetPauseWork(pauseWork);
        }

        /// <summary>
        /// Cancel any loading work for the given ImageView
        /// </summary>
        /// <param name="task">Image loading task to cancel.</param>
        public void CancelWorkFor(IImageLoaderTask task)
        {
            Scheduler.Cancel(task);
        }

        /// <summary>
        /// Removes a pending image loading task from the work queue.
        /// </summary>
        /// <param name="task">Image loading task to remove.</param>
        public void RemovePendingTask(IImageLoaderTask task)
        {
            Scheduler.RemovePendingTask(task);
        }

        /// <summary>
        /// Queue an image loading task.
        /// </summary>
        /// <param name="task">Image loading task.</param>
        public void LoadImage(IImageLoaderTask task)
        {
            Scheduler.LoadImage(task);
			AddRequestToHistory(task);
        }

		/// <summary>
		/// Invalidates selected caches.
		/// </summary>
		/// <returns>An awaitable task.</returns>
		/// <param name="cacheType">Memory cache, Disk cache or both</param>
		public async Task InvalidateCacheAsync(CacheType cacheType)
		{
			InitializeIfNeeded();

			if (cacheType == CacheType.All || cacheType == CacheType.Memory)
			{
				InvalidateMemoryCache();
			}

			if (cacheType == CacheType.All || cacheType == CacheType.Disk)
			{
				await InvalidateDiskCacheAsync().ConfigureAwait(false);
			}
		}

		/// <summary>
		/// Invalidates the memory cache.
		/// </summary>
		public void InvalidateMemoryCache()
		{
			InitializeIfNeeded();
            ImageCache.Instance.Clear();
        }

		/// <summary>
		/// Invalidates the disk cache.
		/// </summary>
		public Task InvalidateDiskCacheAsync()
		{
			InitializeIfNeeded();
			return Config.DiskCache.ClearAsync();
		}

		/// <summary>
		/// Invalidates the cache for given key.
		/// </summary>
		/// <returns>The async.</returns>
		/// <param name="key">Concerns images with this key.</param>
		/// <param name="cacheType">Memory cache, Disk cache or both</param>
		/// <param name="removeSimilar">If similar keys should be removed, ie: typically keys with extra transformations</param>
		public async Task InvalidateCacheEntryAsync(string key, CacheType cacheType, bool removeSimilar=false)
		{
			InitializeIfNeeded();

			if (cacheType == CacheType.All || cacheType == CacheType.Memory)
			{
				ImageCache.Instance.Remove(key);

				if (removeSimilar)
				{
					foreach (var otherKey in _fullKeyToKey.Where(pair => pair.Value == key).Select(pair => pair.Key))
					{
						ImageCache.Instance.Remove(otherKey);
					}
				}
			}

			if (cacheType == CacheType.All || cacheType == CacheType.Disk)
			{
				string hash = _md5Helper.MD5(key);
				await Config.DiskCache.RemoveAsync(hash).ConfigureAwait(false);
			}
		}

		/// <summary>
		/// Downloads the image and adds it to disk cache.
		/// Called only if the cache entry doesn't exist already.
		/// </summary>
		/// <returns>Returns <c>true</c> if added, <c>false</c> otherwise</returns>
		/// <param name="imageUrl">Image URL.</param>
		/// <param name="cancellationToken">Cancellation token.</param>
		/// <param name="duration">Disk cache validity duration.</param>
		/// <param name="customCacheKey">Custom cache key.</param>
		public async Task<bool> DownloadImageAndAddToDiskCacheAsync(string imageUrl, CancellationToken cancellationToken, TimeSpan? duration = null, string customCacheKey = null)
		{
			InitializeIfNeeded();

			string fileName = string.IsNullOrWhiteSpace(customCacheKey) ? _md5Helper.MD5(imageUrl) : _md5Helper.MD5(customCacheKey);

			if (await Config.DiskCache.ExistsAsync(fileName).ConfigureAwait(false))
				return false;

			string filePath = await Config.DiskCache.GetFilePathAsync(fileName).ConfigureAwait(false);
			await Config.DownloadCache.DownloadBytesAndCacheAsync(imageUrl, fileName, filePath, cancellationToken, duration).ConfigureAwait(false);
			return true;
		}

		private void AddRequestToHistory(IImageLoaderTask task)
		{
			AddRequestToHistory(task.Parameters.Path, task.GetKey());
			AddRequestToHistory(task.Parameters.CustomCacheKey, task.GetKey());
			AddRequestToHistory(task.Parameters.LoadingPlaceholderPath, task.GetKey(task.Parameters.LoadingPlaceholderPath));
			AddRequestToHistory(task.Parameters.ErrorPlaceholderPath, task.GetKey(task.Parameters.ErrorPlaceholderPath));
		}

		private void AddRequestToHistory(string baseKey, string fullKey)
		{
			if (string.IsNullOrWhiteSpace(baseKey) || string.IsNullOrWhiteSpace(fullKey))
				return;

			_fullKeyToKey.TryAdd(fullKey, baseKey);
		}
    }
}
