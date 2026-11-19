using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CustomWidgets.WidgetProvider.Services;

namespace CustomWidgets.WidgetProvider.Desktop;

public partial class DesktopHostWindow : Window
{
    private static readonly CultureInfo German = CultureInfo.GetCultureInfo("de-DE");
    private const double DesktopMargin = 28;
    private const double CenterSnapDistance = 18;

    private readonly MediaWatcher _media = new();
    private readonly SystemMetricsService _metrics = new();
    private readonly DesktopLayer _desktopLayer = new();
    private readonly VolumeController _volume = new();
    private readonly DispatcherTimer _systemTimer = new()
    {
        Interval = TimeSpan.FromSeconds(15)
    };
    private readonly DispatcherTimer _volumeTimer = new()
    {
        Interval = TimeSpan.FromSeconds(1)
    };
    private readonly DispatcherTimer _visibilityTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(250)
    };

    private HwndSource? _source;
    private bool? _desktopVisible;
    private int _taskbarCreatedMessage;
    private bool _mediaRefreshInProgress;
    private bool _mediaRefreshPending;
    private bool _systemRefreshInProgress;
    private bool _isDragging;
    private bool _mediaLinkPressed;
    private bool _dragMoved;
    private string? _sourceAppUserModelId;
    private bool _updatingVolumeSlider;
    private int _lastVolumePercent = -1;
    private Point _dragStartScreen;
    private double _dragStartLeft;
    private double _dragStartTop;

    public DesktopHostWindow()
    {
        InitializeComponent();

        DesktopHostSettings? settings = DesktopHostSettings.Load();
        if (settings is not null)
        {
            Left = settings.Left;
            Top = settings.Top;
        }
        else
        {
            Left = SystemParameters.WorkArea.Right - Width - DesktopMargin;
            Top = SystemParameters.WorkArea.Top + DesktopMargin;
        }

        EnsureVisible();

        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
        Closed += OnClosed;
        MouseMove += OnDragMouseMove;
        MouseLeftButtonUp += OnDragMouseLeftButtonUp;
        LostMouseCapture += OnLostMouseCapture;

        _systemTimer.Tick += OnSystemTimerTick;
        _volumeTimer.Tick += OnVolumeTimerTick;
        _visibilityTimer.Tick += OnVisibilityTimerTick;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _source = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
        _source?.AddHook(WndProc);
        _taskbarCreatedMessage = RegisterWindowMessage("TaskbarCreated");

        if (ActualWidth > 0 && ActualHeight > 0)
        {
            _desktopLayer.Attach(this, ActualWidth, ActualHeight);
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_desktopLayer.IsAttached)
        {
            _desktopLayer.Reattach(this, ActualWidth, ActualHeight);
        }
        else
        {
            _desktopLayer.Attach(this, ActualWidth, ActualHeight);
        }

        _media.Changed += OnMediaChanged;
        _ = InitializeMediaAsync();
        RefreshVolume();

        _visibilityTimer.Start();
        UpdateDesktopVisibility(force: true);
        Log.Info("Desktop host window loaded.");
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _systemTimer.Stop();
        _systemTimer.Tick -= OnSystemTimerTick;
        _volumeTimer.Stop();
        _volumeTimer.Tick -= OnVolumeTimerTick;
        _visibilityTimer.Stop();
        _visibilityTimer.Tick -= OnVisibilityTimerTick;
        _media.Changed -= OnMediaChanged;
        _volume.Dispose();
        _source?.RemoveHook(WndProc);
        Log.Info("Desktop host window closed.");
    }

    private async Task InitializeMediaAsync()
    {
        try
        {
            await _media.InitializeAsync();
            await RefreshMediaAsync();
        }
        catch (Exception ex)
        {
            Log.Error("Desktop media initialization failed.", ex);
        }
    }

    private async Task RefreshMediaAsync()
    {
        if (_mediaRefreshInProgress)
        {
            _mediaRefreshPending = true;
            return;
        }

        _mediaRefreshInProgress = true;
        try
        {
            MediaSnapshot snapshot = await _media.GetSnapshotAsync();
            await Dispatcher.InvokeAsync(() => ApplyMedia(snapshot));
        }
        catch (Exception ex)
        {
            Log.Error("Desktop media refresh failed.", ex);
        }
        finally
        {
            _mediaRefreshInProgress = false;
            if (_mediaRefreshPending)
            {
                _mediaRefreshPending = false;
                _ = RefreshMediaAsync();
            }
        }
    }

    private void OnMediaChanged()
    {
        Dispatcher.BeginInvoke(
            DispatcherPriority.Background,
            new Action(() => _ = RefreshMediaAsync()));
    }

    private async Task RefreshSystemAsync()
    {
        if (_systemRefreshInProgress)
        {
            return;
        }

        _systemRefreshInProgress = true;
        try
        {
            SystemSnapshot snapshot = await _metrics.GetSnapshotAsync();
            await Dispatcher.InvokeAsync(() => ApplySystem(snapshot));
        }
        catch (Exception ex)
        {
            Log.Error("Desktop system refresh failed.", ex);
        }
        finally
        {
            _systemRefreshInProgress = false;
        }
    }

    private void ApplyMedia(MediaSnapshot media)
    {
        bool available = media.Status == MediaStatus.Available;
        _sourceAppUserModelId = available ? media.SourceAppUserModelId : null;

        bool canOpenSourceApp = !string.IsNullOrWhiteSpace(_sourceAppUserModelId);
        MediaLinkArea.Cursor = canOpenSourceApp ? Cursors.Hand : Cursors.SizeAll;
        MediaLinkArea.ToolTip = canOpenSourceApp ? "In der Player-App öffnen" : null;
        if (!canOpenSourceApp)
        {
            MediaLinkHighlight.Background = Brushes.Transparent;
        }

        if (available)
        {
            TitleText.Text = string.IsNullOrWhiteSpace(media.Title) ? "Unbekannter Titel" : media.Title;
            ArtistText.Text = string.IsNullOrWhiteSpace(media.Artist) ? "Unbekannter Interpret" : media.Artist;
            AlbumText.Text = media.Album ?? string.Empty;
            AlbumText.Visibility = string.IsNullOrWhiteSpace(media.Album)
                ? Visibility.Collapsed
                : Visibility.Visible;
            MediaMessageText.Visibility = Visibility.Collapsed;
            PlaybackStatusText.Text = media.IsPaused ? "Pausiert" : "Wiedergabe läuft";
            SetCover(media.ArtDataUri);
        }
        else
        {
            (string title, string hint) = media.Status switch
            {
                MediaStatus.AccessDenied => (
                    "Medienzugriff verweigert",
                    "Die Berechtigung für die globale Mediensteuerung fehlt."),
                MediaStatus.Error => (
                    "Medien nicht verfügbar",
                    media.ErrorMessage ?? "Die Wiedergabeinformationen konnten nicht gelesen werden."),
                _ => (
                    "Keine Wiedergabe",
                    "Starten Sie einen Player, um Titel, Interpret und Cover zu sehen.")
            };

            TitleText.Text = title;
            ArtistText.Text = hint;
            AlbumText.Visibility = Visibility.Collapsed;
            MediaMessageText.Visibility = Visibility.Collapsed;
            PlaybackStatusText.Text = string.Empty;
            SetCover(null);
        }

        PreviousButton.IsEnabled = available;
        PlayPauseButton.IsEnabled = available;
        NextButton.IsEnabled = available;
        PlayPauseButton.Content = media.IsPaused ? "\uE768" : "\uE769";
        PlayPauseButton.ToolTip = media.IsPaused ? "Wiedergabe fortsetzen" : "Wiedergabe pausieren";
    }

    private void ApplySystem(SystemSnapshot snapshot)
    {
        CpuValue.Text = Percent(snapshot.CpuPercent);
        CpuDetail.Text = $"{snapshot.LogicalProcessors} logische Kerne";
        CpuBar.Value = snapshot.CpuPercent ?? 0;

        RamValue.Text = Percent(snapshot.MemoryPercent);
        RamDetail.Text = snapshot.MemoryTotalBytes > 0
            ? $"{Gigabytes(snapshot.MemoryUsedBytes)} von {Gigabytes(snapshot.MemoryTotalBytes)} GB"
            : "Nicht verfügbar";
        RamBar.Value = snapshot.MemoryPercent ?? 0;

        BatteryValue.Text = snapshot.BatteryPercent is int batteryPercent
            ? batteryPercent.ToString(German) + " %"
            : "–";
        BatteryBar.Value = snapshot.BatteryPercent ?? 0;
        BatteryDetail.Text = snapshot.Battery switch
        {
            BatteryState.Charging => "Lädt",
            BatteryState.OnAc => "Netzbetrieb",
            BatteryState.OnBattery => "Akkubetrieb",
            BatteryState.NoBattery => "Kein Akku",
            _ => "Nicht verfügbar"
        };

        UpdatedText.Text = $"Aktualisiert um {snapshot.UpdatedAt.ToLocalTime().ToString("HH:mm", German)}";
    }

    private void SetCover(string? dataUri)
    {
        if (string.IsNullOrWhiteSpace(dataUri))
        {
            CoverBrush.ImageSource = null;
            CoverPlaceholder.Visibility = Visibility.Visible;
            return;
        }

        try
        {
            int comma = dataUri.IndexOf(',');
            if (comma < 0)
            {
                throw new FormatException("Cover data URI has no payload.");
            }

            byte[] bytes = Convert.FromBase64String(dataUri[(comma + 1)..]);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = new MemoryStream(bytes);
            image.EndInit();
            image.Freeze();

            CoverBrush.ImageSource = image;
            CoverPlaceholder.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            Log.Warn($"Desktop cover could not be decoded: {ex.GetType().Name}: {ex.Message}");
            CoverBrush.ImageSource = null;
            CoverPlaceholder.Visibility = Visibility.Visible;
        }
    }

    private async void OnPlayPauseClick(object sender, RoutedEventArgs e)
    {
        try
        {
            bool changed = await _media.TogglePlayPauseAsync();
            Log.Info(changed ? "Desktop play/pause toggled." : "Desktop play/pause had no session.");
        }
        catch (Exception ex)
        {
            Log.Error("Desktop play/pause failed.", ex);
        }

        await RefreshMediaAsync();
    }

    private async void OnPreviousClick(object sender, RoutedEventArgs e)
    {
        try
        {
            bool changed = await _media.SkipPreviousAsync();
            Log.Info(changed ? "Desktop previous track requested." : "Desktop previous track had no session.");
        }
        catch (Exception ex)
        {
            Log.Error("Desktop previous track failed.", ex);
        }

        await RefreshMediaAsync();
    }

    private async void OnNextClick(object sender, RoutedEventArgs e)
    {
        try
        {
            bool changed = await _media.SkipNextAsync();
            Log.Info(changed ? "Desktop next track requested." : "Desktop next track had no session.");
        }
        catch (Exception ex)
        {
            Log.Error("Desktop next track failed.", ex);
        }

        await RefreshMediaAsync();
    }

    private void OnMediaLinkMouseEnter(object sender, MouseEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(_sourceAppUserModelId))
        {
            MediaLinkHighlight.Background = (Brush)FindResource("ButtonBrush");
        }
    }

    private void OnMediaLinkMouseLeave(object sender, MouseEventArgs e) =>
        MediaLinkHighlight.Background = Brushes.Transparent;

    private void OpenSourceApp()
    {
        if (string.IsNullOrWhiteSpace(_sourceAppUserModelId))
        {
            return;
        }

        if (!MediaAppLauncher.TryOpen(_sourceAppUserModelId))
        {
            Log.Warn($"Desktop host could not open media source app '{_sourceAppUserModelId}'.");
        }
    }

    private void OnMuteClick(object sender, RoutedEventArgs e)
    {
        if (_volume.ToggleMute(out bool muted))
        {
            Log.Info($"Desktop mute toggled: {muted}.");
            UpdateVolumeUi((int)Math.Round(VolumeSlider.Value), muted);
            return;
        }

        SetVolumeUnavailable();
    }

    private void OnVolumeSliderValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updatingVolumeSlider || !IsLoaded)
        {
            return;
        }

        int percent = (int)Math.Round(e.NewValue);
        if (_volume.SetVolume(percent))
        {
            _lastVolumePercent = percent;
            Log.Info($"Desktop volume set: {percent} %.");
            VolumeValueText.Text = $"{percent} %";
            VolumeSlider.ToolTip = $"Lautstärke: {percent} %";
            return;
        }

        SetVolumeUnavailable();
    }

    private void OnVolumeTimerTick(object? sender, EventArgs e) => RefreshVolume();

    private void RefreshVolume()
    {
        if (!IsLoaded || VolumeSlider.IsMouseCaptureWithin)
        {
            return;
        }

        if (!_volume.TryGetState(out int percent, out bool muted))
        {
            SetVolumeUnavailable();
            return;
        }

        VolumeSlider.IsEnabled = true;
        MuteButton.IsEnabled = true;

        _updatingVolumeSlider = true;
        try
        {
            VolumeSlider.Value = percent;
        }
        finally
        {
            _updatingVolumeSlider = false;
        }

        if (_lastVolumePercent != percent)
        {
            _lastVolumePercent = percent;
            Log.Info($"Desktop volume synced: {percent} % (muted: {muted}).");
        }

        UpdateVolumeUi(percent, muted);
    }

    private void UpdateVolumeUi(int percent, bool muted)
    {
        VolumeValueText.Text = $"{percent} %";
        VolumeSlider.ToolTip = muted
            ? $"Stummgeschaltet ({percent} %)"
            : $"Lautstärke: {percent} %";
        MuteButton.ToolTip = muted ? "Ton einschalten" : "Stummschalten";
        MuteButton.Content = muted ? "\uE74F" : "\uE767";
        MuteButton.Foreground = (Brush)FindResource(
            muted ? "AccentBrush" : "PrimaryTextBrush");
        VolumeMutedText.Visibility = muted ? Visibility.Visible : Visibility.Collapsed;

        string? deviceName = _volume.DeviceName;
        AudioDeviceText.Text = string.IsNullOrWhiteSpace(deviceName)
            ? "Ausgabe: unbekannt"
            : $"Ausgabe: {deviceName}";
        AudioDeviceText.ToolTip = deviceName;
    }

    private void SetVolumeUnavailable()
    {
        VolumeSlider.IsEnabled = false;
        MuteButton.IsEnabled = false;
        VolumeValueText.Text = "–";
        VolumeSlider.ToolTip = "Lautstärke nicht verfügbar";
        MuteButton.ToolTip = "Audioausgabe nicht verfügbar";
        VolumeMutedText.Visibility = Visibility.Collapsed;
        AudioDeviceText.Text = "Ausgabe: nicht verfügbar";
        AudioDeviceText.ToolTip = null;
    }

    private async void OnRefreshClick(object sender, RoutedEventArgs e) => await RefreshSystemAsync();

    private void OnCardMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left ||
            _isDragging ||
            FindButton(e.OriginalSource as DependencyObject) is not null)
        {
            return;
        }

        _dragStartScreen = PointToScreen(e.GetPosition(this));
        _dragStartLeft = Left;
        _dragStartTop = Top;
        _mediaLinkPressed = MediaLinkArea.IsMouseOver;
        _dragMoved = false;
        _isDragging = true;

        if (!CaptureMouse())
        {
            _isDragging = false;
            return;
        }

        e.Handled = true;
    }

    private void OnDragMouseMove(object sender, MouseEventArgs e)
    {
        if (!_isDragging)
        {
            return;
        }

        Point currentScreen = PointToScreen(e.GetPosition(this));
        if (!_dragMoved &&
            (Math.Abs(currentScreen.X - _dragStartScreen.X) > 4 ||
             Math.Abs(currentScreen.Y - _dragStartScreen.Y) > 4))
        {
            _dragMoved = true;
        }

        DpiScale dpi = VisualTreeHelper.GetDpi(this);
        double left = _dragStartLeft + (currentScreen.X - _dragStartScreen.X) / dpi.DpiScaleX;
        double top = _dragStartTop + (currentScreen.Y - _dragStartScreen.Y) / dpi.DpiScaleY;

        // Snap to the vertical center line of the screen, like an alignment
        // guide in PowerPoint. The vertical position stays free.
        Rect workArea = SystemParameters.WorkArea;
        double centeredLeft = workArea.Left + (workArea.Width - Width) / 2;
        if (Math.Abs(left - centeredLeft) <= CenterSnapDistance)
        {
            left = centeredLeft;
        }

        Left = left;
        Top = top;
        e.Handled = true;
    }

    private void OnDragMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isDragging || e.ChangedButton != MouseButton.Left)
        {
            return;
        }

        bool openSourceApp = !_dragMoved && _mediaLinkPressed;
        _mediaLinkPressed = false;
        EndDrag();

        if (openSourceApp)
        {
            OpenSourceApp();
        }

        e.Handled = true;
    }

    private void OnLostMouseCapture(object sender, MouseEventArgs e) => EndDrag();

    private void EndDrag()
    {
        if (!_isDragging)
        {
            return;
        }

        _isDragging = false;
        if (IsMouseCaptured)
        {
            ReleaseMouseCapture();
        }

        EnsureVisible();
        DesktopHostSettings.Save(Left, Top);
    }

    private void EnsureVisible()
    {
        double width = Width > 0 ? Width : 820;
        double height = Height > 0 ? Height : 300;
        double minLeft = SystemParameters.VirtualScreenLeft;
        double minTop = SystemParameters.VirtualScreenTop;
        double maxLeft = minLeft + SystemParameters.VirtualScreenWidth - width;
        double maxTop = minTop + SystemParameters.VirtualScreenHeight - height;

        if (maxLeft < minLeft)
        {
            maxLeft = minLeft;
        }

        if (maxTop < minTop)
        {
            maxTop = minTop;
        }

        Left = Math.Clamp(Left, minLeft, maxLeft);
        Top = Math.Clamp(Top, minTop, maxTop);
    }

    private static Button? FindButton(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is Button button)
            {
                return button;
            }

            source = VisualTreeHelper.GetParent(source);
        }

        return null;
    }

    private async void OnSystemTimerTick(object? sender, EventArgs e) => await RefreshSystemAsync();

    private void OnVisibilityTimerTick(object? sender, EventArgs e) => UpdateDesktopVisibility();

    private void UpdateDesktopVisibility(bool force = false)
    {
        bool visible = IsDesktopForeground();
        if (!force && _desktopVisible == visible)
        {
            return;
        }

        _desktopVisible = visible;
        Opacity = visible ? 1 : 0;
        _desktopLayer.SetDesktopVisible(visible);

        if (visible)
        {
            if (!_systemTimer.IsEnabled)
            {
                _systemTimer.Start();
            }

            if (!_volumeTimer.IsEnabled)
            {
                _volumeTimer.Start();
            }

            RefreshVolume();
            _ = RefreshSystemAsync();
        }
        else
        {
            _systemTimer.Stop();
            _volumeTimer.Stop();
        }
    }

    private bool IsDesktopForeground()
    {
        IntPtr foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero)
        {
            return false;
        }

        if (foreground == GetShellWindow() || foreground == new WindowInteropHelper(this).Handle)
        {
            return true;
        }

        var className = new StringBuilder(256);
        GetClassName(foreground, className, className.Capacity);
        string value = className.ToString();
        return value is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "SysListView32";
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == _taskbarCreatedMessage || msg == 0x007E || msg == 0x001A)
        {
            Dispatcher.BeginInvoke(
                DispatcherPriority.Background,
                new Action(() =>
                {
                    if (IsVisible && ActualWidth > 0 && ActualHeight > 0)
                    {
                        EnsureVisible();
                        _desktopLayer.Reattach(this, ActualWidth, ActualHeight);
                        _desktopVisible = null;
                        UpdateDesktopVisibility(force: true);
                    }
                }));
        }

        return IntPtr.Zero;
    }

    private static string Percent(double? value) =>
        value is null ? "–" : value.Value.ToString("0", German) + " %";

    private static string Gigabytes(ulong bytes) =>
        (bytes / (1024.0 * 1024.0 * 1024.0)).ToString("0.0", German);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegisterWindowMessage(string message);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr GetShellWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder className, int maxCount);
}
