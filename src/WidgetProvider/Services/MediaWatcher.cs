using System.Runtime.InteropServices;
using Windows.Graphics.Imaging;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace CustomWidgets.WidgetProvider.Services;

internal enum MediaStatus
{
    Available,
    NoSession,
    AccessDenied,
    Error
}

internal sealed record MediaSnapshot(
    MediaStatus Status,
    string Title = "",
    string Artist = "",
    string Album = "",
    string? ArtDataUri = null,
    bool IsPaused = false,
    string? ErrorMessage = null,
    string? SourceAppUserModelId = null)
{
    public static MediaSnapshot NoSession { get; } = new(MediaStatus.NoSession);
}

/// <summary>
/// Tracks the system-wide media session. Updates are event driven: the provider
/// subscribes to session changes and media-property changes and never polls.
/// </summary>
internal sealed class MediaWatcher
{
    private const double MaxArtworkSize = 144;

    private readonly object _gate = new();
    private readonly object _artGate = new();
    private readonly SemaphoreSlim _initGate = new(1, 1);

    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;
    private bool _accessDenied;

    private string? _artKey;
    private string? _artDataUri;

    /// <summary>Raised on the current thread whenever the session or its metadata changes.</summary>
    public event Action? Changed;

    public async Task InitializeAsync()
    {
        await EnsureManagerAsync().ConfigureAwait(false);
    }

    public async Task<MediaSnapshot> GetSnapshotAsync()
    {
        GlobalSystemMediaTransportControlsSessionManager? manager = await EnsureManagerAsync().ConfigureAwait(false);
        if (manager is null)
        {
            return _accessDenied
                ? new MediaSnapshot(
                    MediaStatus.AccessDenied,
                    ErrorMessage: "Windows hat den Zugriff auf die Medienwiedergabe verweigert.")
                : new MediaSnapshot(
                    MediaStatus.Error,
                    ErrorMessage: "Die Mediensteuerung ist derzeit nicht verfügbar.");
        }

        try
        {
            GlobalSystemMediaTransportControlsSession? session = manager.GetCurrentSession();
            if (session is null)
            {
                AttachSession(null);
                return MediaSnapshot.NoSession;
            }

            AttachSession(session);

            GlobalSystemMediaTransportControlsSessionMediaProperties properties =
                await session.TryGetMediaPropertiesAsync();

            string title = FirstNonEmpty(properties.Title, "Unbekannter Titel");
            string artist = FirstNonEmpty(properties.Artist, properties.AlbumArtist, "Unbekannter Interpret");
            string album = properties.AlbumTitle ?? string.Empty;

            bool isPaused;
            try
            {
                isPaused = session.GetPlaybackInfo()?.PlaybackStatus ==
                    GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused;
            }
            catch
            {
                isPaused = false;
            }

            string? art = await GetArtworkAsync(properties).ConfigureAwait(false);
            return new MediaSnapshot(
                MediaStatus.Available,
                title,
                artist,
                album,
                art,
                isPaused,
                SourceAppUserModelId: TryGetSourceAppUserModelId(session));
        }
        catch (Exception ex) when (IsAccessDenied(ex))
        {
            _accessDenied = true;
            Log.Error("Access to the media session was denied.", ex);
            return new MediaSnapshot(
                MediaStatus.AccessDenied,
                ErrorMessage: "Windows hat den Zugriff auf die Medienwiedergabe verweigert.");
        }
        catch (Exception ex)
        {
            Log.Error("Reading the current media session failed.", ex);
            return new MediaSnapshot(
                MediaStatus.Error,
                ErrorMessage: "Die Wiedergabeinformationen konnten nicht gelesen werden.");
        }
    }

    /// <summary>Toggles play/pause on the current system media session.</summary>
    public async Task<bool> TogglePlayPauseAsync()
    {
        GlobalSystemMediaTransportControlsSession? session = await GetCurrentSessionAsync().ConfigureAwait(false);
        if (session is null)
        {
            return false;
        }

        try
        {
            return await session.TryTogglePlayPauseAsync();
        }
        catch (Exception ex)
        {
            Log.Error("Toggling media playback failed.", ex);
            return false;
        }
    }

    /// <summary>Skips to the next track in the current system media session.</summary>
    public async Task<bool> SkipNextAsync()
    {
        GlobalSystemMediaTransportControlsSession? session = await GetCurrentSessionAsync().ConfigureAwait(false);
        if (session is null)
        {
            return false;
        }

        try
        {
            return await session.TrySkipNextAsync();
        }
        catch (Exception ex)
        {
            Log.Error("Skipping to the next track failed.", ex);
            return false;
        }
    }

    /// <summary>Skips to the previous track in the current system media session.</summary>
    public async Task<bool> SkipPreviousAsync()
    {
        GlobalSystemMediaTransportControlsSession? session = await GetCurrentSessionAsync().ConfigureAwait(false);
        if (session is null)
        {
            return false;
        }

        try
        {
            return await session.TrySkipPreviousAsync();
        }
        catch (Exception ex)
        {
            Log.Error("Skipping to the previous track failed.", ex);
            return false;
        }
    }

    private async Task<GlobalSystemMediaTransportControlsSession?> GetCurrentSessionAsync()
    {
        GlobalSystemMediaTransportControlsSessionManager? manager = await EnsureManagerAsync().ConfigureAwait(false);
        return manager?.GetCurrentSession();
    }

    private async Task<GlobalSystemMediaTransportControlsSessionManager?> EnsureManagerAsync()
    {
        GlobalSystemMediaTransportControlsSessionManager? existing = Volatile.Read(ref _manager);
        if (existing is not null)
        {
            return existing;
        }

        await _initGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_manager is not null)
            {
                return _manager;
            }

            GlobalSystemMediaTransportControlsSessionManager manager =
                await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();

            manager.CurrentSessionChanged += OnCurrentSessionChanged;
            _manager = manager;
            AttachSession(manager.GetCurrentSession());
            Log.Info("Global media session manager initialized.");
            return manager;
        }
        catch (Exception ex)
        {
            _accessDenied = IsAccessDenied(ex);
            Log.Error(
                _accessDenied
                    ? "Global media session manager: access denied. Is the globalMediaControl capability declared?"
                    : "Global media session manager initialization failed.",
                ex);
            return null;
        }
        finally
        {
            _initGate.Release();
        }
    }

    private void OnCurrentSessionChanged(
        GlobalSystemMediaTransportControlsSessionManager sender,
        CurrentSessionChangedEventArgs args)
    {
        try
        {
            AttachSession(sender.GetCurrentSession());
            RaiseChanged();
        }
        catch (Exception ex)
        {
            Log.Error("Current media session changed handler failed.", ex);
        }
    }

    private void OnMediaPropertiesChanged(
        GlobalSystemMediaTransportControlsSession sender,
        MediaPropertiesChangedEventArgs args) => RaiseChanged();

    private void OnPlaybackInfoChanged(
        GlobalSystemMediaTransportControlsSession sender,
        PlaybackInfoChangedEventArgs args) => RaiseChanged();

    private void AttachSession(GlobalSystemMediaTransportControlsSession? session)
    {
        bool changed;
        lock (_gate)
        {
            if (ReferenceEquals(_session, session))
            {
                return;
            }

            if (_session is not null)
            {
                _session.MediaPropertiesChanged -= OnMediaPropertiesChanged;
                _session.PlaybackInfoChanged -= OnPlaybackInfoChanged;
            }

            _session = session;

            if (session is not null)
            {
                session.MediaPropertiesChanged += OnMediaPropertiesChanged;
                session.PlaybackInfoChanged += OnPlaybackInfoChanged;
            }

            changed = true;
        }

        if (changed)
        {
            RaiseChanged();
        }
    }

    private void RaiseChanged()
    {
        try
        {
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            Log.Error("A media change subscriber failed.", ex);
        }
    }

    private async Task<string?> GetArtworkAsync(
        GlobalSystemMediaTransportControlsSessionMediaProperties properties)
    {
        string key = string.Join(
            '\u001f',
            properties.Title ?? string.Empty,
            properties.Artist ?? string.Empty,
            properties.AlbumTitle ?? string.Empty);

        lock (_artGate)
        {
            if (string.Equals(_artKey, key, StringComparison.Ordinal))
            {
                return _artDataUri;
            }
        }

        string? dataUri = await TryEncodeArtworkAsync(properties.Thumbnail).ConfigureAwait(false);

        lock (_artGate)
        {
            _artKey = key;
            _artDataUri = dataUri;
        }

        return dataUri;
    }

    private static async Task<string?> TryEncodeArtworkAsync(IRandomAccessStreamReference? reference)
    {
        if (reference is null)
        {
            return null;
        }

        try
        {
            using IRandomAccessStreamWithContentType stream = await reference.OpenReadAsync();
            BitmapDecoder decoder = await BitmapDecoder.CreateAsync(stream);
            if (decoder.PixelWidth == 0 || decoder.PixelHeight == 0)
            {
                return null;
            }

            double scale = Math.Min(
                1.0,
                Math.Min(MaxArtworkSize / decoder.PixelWidth, MaxArtworkSize / decoder.PixelHeight));

            var transform = new BitmapTransform
            {
                ScaledWidth = (uint)Math.Max(1, Math.Round(decoder.PixelWidth * scale)),
                ScaledHeight = (uint)Math.Max(1, Math.Round(decoder.PixelHeight * scale)),
                InterpolationMode = BitmapInterpolationMode.Fant
            };

            using SoftwareBitmap bitmap = await decoder.GetSoftwareBitmapAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied,
                transform,
                ExifOrientationMode.IgnoreExifOrientation,
                ColorManagementMode.DoNotColorManage);

            using var output = new InMemoryRandomAccessStream();
            BitmapEncoder encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, output);
            encoder.SetSoftwareBitmap(bitmap);
            await encoder.FlushAsync();

            if (output.Size == 0 || output.Size > 300_000)
            {
                return null;
            }

            var bytes = new byte[output.Size];
            using (var reader = new DataReader(output.GetInputStreamAt(0)))
            {
                await reader.LoadAsync((uint)output.Size);
                reader.ReadBytes(bytes);
            }

            return "data:image/png;base64," + Convert.ToBase64String(bytes);
        }
        catch (Exception ex)
        {
            Log.Warn($"Cover artwork could not be embedded: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    private static string? TryGetSourceAppUserModelId(GlobalSystemMediaTransportControlsSession session)
    {
        try
        {
            string id = session.SourceAppUserModelId;
            return string.IsNullOrWhiteSpace(id) ? null : id.Trim();
        }
        catch (Exception ex)
        {
            Log.Warn($"Source app id could not be read: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (string? value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return string.Empty;
    }

    private static bool IsAccessDenied(Exception exception)
    {
        const int AccessDeniedHResult = unchecked((int)0x80070005);

        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is UnauthorizedAccessException || current.HResult == AccessDeniedHResult)
            {
                return true;
            }
        }

        return false;
    }
}
