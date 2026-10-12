using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using revit_mcp_plugin.Core;
using revit_mcp_plugin.Fmc;
using revit_mcp_plugin.Utils;

namespace revit_mcp_plugin.UI
{
    /// <summary>
    /// Content of the Formcraft Browser dockable pane. Server state, activity and the guard file are polled once a
    /// second (cheap reads, redrawn only on change); the active document is pushed in by FormcraftBrowser's Idling hook.
    /// Nothing here calls the Revit API: Start/Stop goes through an ExternalEvent.
    /// </summary>
    public partial class FormcraftBrowserPage : UserControl
    {
        public event Action ToggleServerRequested;

        private readonly DispatcherTimer _timer;
        private GuardStatus _guard;
        private DocState _doc;
        private long _activityVersion = -1;
        private bool? _wasRunning;
        private DateTime? _pendingSince;

        public FormcraftBrowserPage()
        {
            InitializeComponent();
            _guard = GuardStatus.Load();
            ShowGuard();
            ShowLock();

            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _timer.Tick += (s, e) => Refresh();
            _timer.Start();
            Refresh();
        }

        public void SetDocument(DocState doc)
        {
            _doc = doc;
            if (doc == null)
            {
                DocTitle.Text = "No document open";
                DocKind.Text = "";
                DocPath.Text = "";
            }
            else
            {
                DocTitle.Text = doc.Title;
                DocKind.Text = (doc.IsFamily ? "Family" : "Project or template") +
                               (doc.IsWorkshared ? " · workshared" : "") +
                               (string.IsNullOrEmpty(doc.Path) ? " · not saved yet" : "");
                DocPath.Text = string.IsNullOrEmpty(doc.Path) ? "" : Path.GetDirectoryName(doc.Path);
            }
            DocPath.Visibility = DocPath.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            DocKind.Visibility = DocKind.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            ShowLock();
        }

        public void ApplyTheme(bool dark)
        {
            void Set(string key, string hex) => Resources[key] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            Set("Bg", dark ? "#2B3340" : "#FFFFFF");
            Set("Surface", dark ? "#343D4B" : "#F4F5F7");
            Set("Border", dark ? "#4A5466" : "#DADDE2");
            Set("Text", dark ? "#E8ECF1" : "#1B2532");
            Set("Muted", dark ? "#A3ADBA" : "#5B6573");
            Set("Good", dark ? "#5CC27A" : "#2E7D32");
            Set("Warn", dark ? "#E8A33D" : "#A15C00");
            Set("Bad", dark ? "#F0776A" : "#C62828");
            Set("Hover", dark ? "#3F4958" : "#E6E9EE");
            // Dots are set from code, so repaint them with the new brushes.
            _wasRunning = null;
            _activityVersion = -1;
            ShowGuard();
            ShowLock();
            Refresh();
        }

        private Brush B(string key) => (Brush)Resources[key];

        private void Refresh()
        {
            SocketService service = SocketService.Instance;
            bool running = service.IsRunning;
            if (_pendingSince.HasValue && (_wasRunning != running || DateTime.Now - _pendingSince.Value > TimeSpan.FromSeconds(5)))
                _pendingSince = null;

            if (_wasRunning != running)
            {
                _wasRunning = running;
                ServerDot.Fill = running ? B("Good") : B("Muted");
                ServerText.Text = running ? "Running" : "Stopped";
                ServerButton.Content = running ? "Stop" : "Start";
            }
            ServerButton.IsEnabled = !_pendingSince.HasValue;

            var last = ActivityLog.Snapshot().FirstOrDefault();
            string lastCall = last == null ? "no calls yet" : "last call " + Ago(last.Time);
            ServerDetail.Text = running
                ? $"Port {service.Port} · {lastCall}"
                : "Start it so Claude can reach Revit";

            if (ActivityLog.Version != _activityVersion)
            {
                _activityVersion = ActivityLog.Version;
                var rows = ActivityLog.Snapshot().Select(a => new
                {
                    a.Method,
                    Right = $"{a.Milliseconds} ms · {a.Time:HH:mm:ss}",
                    Dot = a.Ok ? B("Good") : B("Bad"),
                    Error = a.Error ?? "",
                    ErrorVisibility = string.IsNullOrEmpty(a.Error) ? Visibility.Collapsed : Visibility.Visible,
                    Tip = a.Ok ? $"{a.Method} succeeded at {a.Time:HH:mm:ss}" : $"{a.Method} failed: {a.Error}",
                }).ToList();
                ActivityList.ItemsSource = rows;
                ActivityEmpty.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            }

            // Reload the guard when the file appears, disappears or is saved.
            string path = GuardStatus.DefaultConfigPath();
            DateTime stamp = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
            if (path != _guard.ConfigPath || stamp != _guard.LastWrite)
            {
                _guard = GuardStatus.Load(path);
                ShowGuard();
                ShowLock();
            }
        }

        private static string Ago(DateTime t)
        {
            var d = DateTime.Now - t;
            if (d.TotalSeconds < 60) return "just now";
            if (d.TotalMinutes < 60) return $"{(int)d.TotalMinutes} min ago";
            return $"at {t:HH:mm}";
        }

        private void ShowGuard()
        {
            if (_guard.Found)
            {
                GuardDot.Fill = _guard.LiveModelLock ? B("Good") : B("Warn");
                GuardText.Text = _guard.LiveModelLock ? "Loaded · live-model lock on" : "Loaded · live-model lock OFF";
                GuardDetail.Text = _guard.ConfigPath;
            }
            else
            {
                GuardDot.Fill = B("Bad");
                GuardText.Text = _guard.Problem;
                GuardDetail.Text = _guard.ConfigPath;
            }
            WriteExpander.Header = $"Write folders ({_guard.WriteRoots.Count})";
            WriteList.ItemsSource = _guard.WriteRoots;
            LibraryExpander.Header = $"Library folders ({_guard.LibraryRoots.Count})";
            LibraryList.ItemsSource = _guard.LibraryRoots;
        }

        private void ShowLock()
        {
            var (ok, reason) = _guard.Verdict(_doc);
            LockDot.Fill = _doc == null ? B("Muted") : ok ? B("Good") : B("Warn");
            LockText.Text = _doc == null ? "Model tools: —" : ok ? "Model tools: allowed" : "Model tools: locked";
            LockReason.Text = reason;
        }

        private void ServerButton_Click(object sender, RoutedEventArgs e)
        {
            _pendingSince = DateTime.Now;
            ServerButton.IsEnabled = false;
            ToggleServerRequested?.Invoke();
        }

        private void ClearActivity_Click(object sender, RoutedEventArgs e) => ActivityLog.Clear();

        private void OpenGuard_Click(object sender, RoutedEventArgs e)
        {
            string path = _guard.ConfigPath;
            if (!File.Exists(path))
            {
                MessageBox.Show(
                    $"There is no guard file yet at\n{path}\n\nCopy fmc_guard.example.json there and edit the folders. " +
                    "Until it exists every file write is refused.",
                    FormcraftBrowser.Title, MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            Launch("notepad.exe", Quote(path));
        }

        private void OpenReports_Click(object sender, RoutedEventArgs e)
        {
            string dir = GuardStatus.ExpandEnv(_guard.ReportsDir);
            if (!Directory.Exists(dir))
            {
                MessageBox.Show($"No reports yet: {dir} does not exist.", FormcraftBrowser.Title,
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            Launch("explorer.exe", Quote(dir));
        }

        private void OpenLogs_Click(object sender, RoutedEventArgs e) =>
            Launch("explorer.exe", Quote(PathManager.GetLogsDirectoryPath()));

        private void OpenSettings_Click(object sender, RoutedEventArgs e)
        {
            var window = new SettingsWindow();
            _ = new System.Windows.Interop.WindowInteropHelper(window)
            {
                Owner = Process.GetCurrentProcess().MainWindowHandle
            };
            window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            window.Show();
        }

        private static string Quote(string s) => "\"" + s + "\"";

        private static void Launch(string exe, string args)
        {
            try { Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = true }); }
            catch (Exception ex) { MessageBox.Show(ex.Message, FormcraftBrowser.Title); }
        }
    }
}
