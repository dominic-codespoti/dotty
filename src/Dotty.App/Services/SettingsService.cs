using System;
using System.IO;
using System.Text.Json;
using System.Timers;

namespace Dotty.App.Services
{
    public class SettingsService : IDisposable
    {
        public UserSettings Current { get; private set; } = new UserSettings();

        private readonly string _path;
        private FileSystemWatcher? _watcher;
        private Timer? _debounceTimer;

        // Raised when settings are loaded or reloaded from disk
        public event Action<UserSettings>? SettingsChanged;

        public SettingsService()
        {
            var configHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            if (string.IsNullOrEmpty(configHome))
            {
                var home = Environment.GetFolderPath(Environment.SpecialFolder.Personal);
                configHome = Path.Combine(home, ".config");
            }

            var dir = Path.Combine(configHome, "dotty");
            _path = Path.Combine(dir, "settings.json");

            // Create directory if missing
            try
            {
                if (!Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
            }
            catch
            {
                // ignore
            }

            // Ensure a file exists (write defaults) so users can edit it
            if (!File.Exists(_path))
            {
                Save();
            }
        }

        public void Load()
        {
            try
            {
                if (File.Exists(_path))
                {
                    var json = File.ReadAllText(_path);
                    var parsed = JsonSerializer.Deserialize<UserSettings>(json, SettingsJsonContext.Default.UserSettings);
                    if (parsed != null)
                        Current = parsed;
                }
                else
                {
                    // create file with defaults
                    Save();
                }

                SettingsChanged?.Invoke(Current);
            }
            catch
            {
                // ignore - keep defaults
            }
        }

        public void Save()
        {
            try
            {
                var dir = Path.GetDirectoryName(_path)!;
                if (!Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                var json = JsonSerializer.Serialize(Current, SettingsJsonContext.Default.UserSettings);
                File.WriteAllText(_path, json);
            }
            catch
            {
                // ignore - best effort save
            }
        }

        /// <summary>
        /// Start a background FileSystemWatcher to monitor the settings file and auto-reload when it changes.
        /// </summary>
        public void StartWatching()
        {
            try
            {
                var dir = Path.GetDirectoryName(_path)!;
                var file = Path.GetFileName(_path)!;

                _watcher = new FileSystemWatcher(dir, file)
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.CreationTime,
                    EnableRaisingEvents = true,
                };

                _watcher.Changed += OnFileChanged;
                _watcher.Created += OnFileChanged;
                _watcher.Renamed += OnFileChanged;
                _watcher.Deleted += OnFileChanged;

                // debounce timer to collapse rapid events
                _debounceTimer = new Timer(250) { AutoReset = false };
                _debounceTimer.Elapsed += (s, e) => Load();
            }
            catch
            {
                // ignore - best effort
            }
        }

        private void OnFileChanged(object sender, FileSystemEventArgs e)
        {
            try
            {
                // restart debounce timer
                if (_debounceTimer != null)
                {
                    _debounceTimer.Stop();
                    _debounceTimer.Start();
                }
                else
                {
                    // fallback: immediate reload
                    Load();
                }
            }
            catch
            {
                // ignore
            }
        }

        public void Dispose()
        {
            try
            {
                if (_watcher != null)
                {
                    _watcher.EnableRaisingEvents = false;
                    _watcher.Dispose();
                    _watcher = null;
                }

                if (_debounceTimer != null)
                {
                    _debounceTimer.Stop();
                    _debounceTimer.Dispose();
                    _debounceTimer = null;
                }
            }
            catch { }
        }
    }
}
