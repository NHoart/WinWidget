using System.Runtime.InteropServices;
using CustomWidgets.WidgetProvider.Services;
using Microsoft.Windows.Widgets;
using Microsoft.Windows.Widgets.Providers;

namespace CustomWidgets.WidgetProvider;

/// <summary>
/// Widget provider implementation. The Widgets Board creates this class through
/// COM. All state is static because the host may create more than one provider
/// instance during the lifetime of the process.
/// </summary>
[ComVisible(true)]
[Guid(ClassIdString)]
internal sealed class WidgetProvider : IWidgetProvider
{
    public const string ClassIdString = "59D3A472-B0E7-45FC-865B-68863B7D3DC3";
    public static readonly Guid ClassId = new(ClassIdString);

    private static readonly object Gate = new();
    private static readonly Dictionary<string, WidgetState> Widgets = new(StringComparer.Ordinal);
    private static readonly ManualResetEventSlim NoWidgetsEvent = new(false);
    private static readonly SemaphoreSlim RefreshGate = new(1, 1);
    private static readonly MediaWatcher Media = new();
    private static readonly SystemMetricsService Metrics = new();

    private static int _initialized;
    private static CancellationTokenSource? _mediaDebounce;

    static WidgetProvider()
    {
        Media.Changed += OnMediaChanged;
    }

    public WidgetProvider()
    {
        RecoverRunningWidgets();

        // A restarted provider may already own pinned widgets. Push fresh
        // content immediately instead of waiting for the next Activate.
        if (HasWidgets)
        {
            _ = RefreshDefinitionAsync(WidgetIds.NowPlaying);
            _ = RefreshDefinitionAsync(WidgetIds.System);
        }
    }

    public static bool HasWidgets
    {
        get
        {
            lock (Gate)
            {
                return Widgets.Count > 0;
            }
        }
    }

    public static bool WaitForNoWidgets(TimeSpan timeout) => NoWidgetsEvent.Wait(timeout);

    /// <summary>Starts media tracking and pushes fresh content for recovered widgets.</summary>
    public static void Initialize()
    {
        if (Interlocked.Exchange(ref _initialized, 1) != 0)
        {
            return;
        }

        Log.Info("Widget provider initialized.");
        _ = Media.InitializeAsync();
        _ = RefreshDefinitionAsync(WidgetIds.NowPlaying);
        _ = RefreshDefinitionAsync(WidgetIds.System);
    }

    public void CreateWidget(WidgetContext widgetContext)
    {
        RunSafely("CreateWidget", () =>
        {
            Track(widgetContext);
            InvalidateTemplate(widgetContext.Id);
            NoWidgetsEvent.Reset();
            _ = RefreshDefinitionAsync(widgetContext.DefinitionId);
        });
    }

    public void DeleteWidget(string widgetId, string customState)
    {
        RunSafely("DeleteWidget", () =>
        {
            bool empty;
            lock (Gate)
            {
                Widgets.Remove(widgetId);
                empty = Widgets.Count == 0;
            }

            if (empty)
            {
                Log.Info("The last pinned widget was removed.");
                NoWidgetsEvent.Set();
            }
        });
    }

    public void Activate(WidgetContext widgetContext)
    {
        RunSafely("Activate", () =>
        {
            Track(widgetContext);
            _ = RefreshDefinitionAsync(widgetContext.DefinitionId);
        });
    }

    public void Deactivate(string widgetId)
    {
        // There is no periodic work to stop: all updates are event driven. The
        // host may still ask for updates after Deactivate, so keep serving them.
        Log.Info($"Widget {widgetId} deactivated.");
    }

    public void OnActionInvoked(WidgetActionInvokedArgs actionInvokedArgs)
    {
        RunSafely(
            "OnActionInvoked",
            () => HandleAction(actionInvokedArgs.Verb, actionInvokedArgs.WidgetContext.Id));
    }

    /// <summary>Handles a card verb; kept separate so the action path is testable.</summary>
    internal static void HandleAction(string? verb, string? widgetId = null)
    {
        if (string.Equals(verb, Cards.OpenSourceAppVerb, StringComparison.Ordinal))
        {
            _ = OpenSourceAppAsync();
            return;
        }

        Log.Info($"Unhandled widget action '{verb}' for {widgetId}.");
    }

    /// <summary>Opens the app that is currently playing, as requested by the card action.</summary>
    private static async Task OpenSourceAppAsync()
    {
        try
        {
            MediaSnapshot media = await Media.GetSnapshotAsync().ConfigureAwait(false);
            if (!MediaAppLauncher.TryOpen(media.SourceAppUserModelId))
            {
                Log.Warn("The now-playing widget action found no app to open.");
            }
        }
        catch (Exception ex)
        {
            Log.Error("Opening the media source app from a widget action failed.", ex);
        }
    }

    public void OnWidgetContextChanged(WidgetContextChangedArgs contextChangedArgs)
    {
        RunSafely("OnWidgetContextChanged", () =>
        {
            WidgetContext widgetContext = contextChangedArgs.WidgetContext;
            Track(widgetContext);
            _ = RefreshDefinitionAsync(widgetContext.DefinitionId);
        });
    }

    private static void RecoverRunningWidgets()
    {
        try
        {
            IReadOnlyList<WidgetInfo> infos = WidgetManager.GetDefault().GetWidgetInfos();
            int recovered = 0;

            lock (Gate)
            {
                foreach (WidgetInfo info in infos)
                {
                    WidgetContext context = info.WidgetContext;
                    if (string.IsNullOrEmpty(context.Id))
                    {
                        continue;
                    }

                    Widgets[context.Id] = new WidgetState(context.Id, context.DefinitionId, context.Size);
                    recovered++;
                }
            }

            Log.Info($"Recovered {recovered} pinned widget(s).");
        }
        catch (Exception ex)
        {
            Log.Error("Could not query running widgets during startup.", ex);
        }
    }

    private static void InvalidateTemplate(string widgetId)
    {
        lock (Gate)
        {
            if (Widgets.TryGetValue(widgetId, out WidgetState? state))
            {
                state.LastTemplate = null;
            }
        }
    }

    private static void Track(WidgetContext context)
    {
        lock (Gate)
        {
            if (Widgets.TryGetValue(context.Id, out WidgetState? state))
            {
                state.DefinitionId = context.DefinitionId;
                state.Size = context.Size;
                return;
            }

            Widgets[context.Id] = new WidgetState(context.Id, context.DefinitionId, context.Size);
        }
    }

    /// <summary>
    /// Fetches data once, then updates every pinned instance of the definition.
    /// Calls are serialized so a host that invokes callbacks concurrently cannot
    /// interleave two updates.
    /// </summary>
    private static async Task RefreshDefinitionAsync(string definitionId)
    {
        await RefreshGate.WaitAsync().ConfigureAwait(false);
        try
        {
            List<WidgetSnapshot> targets;
            lock (Gate)
            {
                targets = Widgets.Values
                    .Where(widget => string.Equals(widget.DefinitionId, definitionId, StringComparison.Ordinal))
                    .Select(widget => new WidgetSnapshot(widget.Id, widget.DefinitionId, widget.Size))
                    .ToList();
            }

            if (targets.Count == 0)
            {
                return;
            }

            switch (definitionId)
            {
                case WidgetIds.NowPlaying:
                {
                    MediaSnapshot media = await Media.GetSnapshotAsync().ConfigureAwait(false);
                    foreach (WidgetSnapshot target in targets)
                    {
                        await PushAsync(target.Id, Cards.NowPlaying(media, target.Size)).ConfigureAwait(false);
                    }

                    break;
                }

                case WidgetIds.System:
                {
                    SystemSnapshot metrics = await Metrics.GetSnapshotAsync().ConfigureAwait(false);
                    foreach (WidgetSnapshot target in targets)
                    {
                        await PushAsync(target.Id, Cards.SystemWidget(metrics, target.Size)).ConfigureAwait(false);
                    }

                    break;
                }

                default:
                    Log.Warn($"Widget definition '{definitionId}' is not handled by this provider.");
                    break;
            }
        }
        catch (Exception ex)
        {
            Log.Error($"Refreshing widget definition '{definitionId}' failed.", ex);
        }
        finally
        {
            RefreshGate.Release();
        }
    }

    private static async Task PushAsync(string widgetId, string template)
    {
        lock (Gate)
        {
            if (!Widgets.TryGetValue(widgetId, out WidgetState? state))
            {
                return;
            }

            if (string.Equals(state.LastTemplate, template, StringComparison.Ordinal))
            {
                // Nothing changed since the last successful update.
                return;
            }
        }

        try
        {
            var options = new WidgetUpdateRequestOptions(widgetId)
            {
                Template = template,
                Data = "{}",
                CustomState = string.Empty,
                IsPlaceholderContent = false
            };

            WidgetManager.GetDefault().UpdateWidget(options);

            lock (Gate)
            {
                if (Widgets.TryGetValue(widgetId, out WidgetState? current))
                {
                    current.LastTemplate = template;
                }
            }
        }
        catch (Exception ex)
        {
            // A widget can be disabled or removed between the callback and this
            // update. That is expected; log it and keep the provider running.
            Log.Warn(
                $"Widget update for '{widgetId}' was rejected (widget may be disabled): " +
                $"{ex.GetType().Name}: {ex.Message} (0x{ex.HResult:X8})");
        }

        await Task.CompletedTask;
    }

    private static void OnMediaChanged()
    {
        var cts = new CancellationTokenSource();
        CancellationTokenSource? previous = Interlocked.Exchange(ref _mediaDebounce, cts);

        try
        {
            previous?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The previous debounce task already finished.
        }

        _ = Task.Run(async () =>
        {
            try
            {
                // MediaPropertiesChanged and PlaybackInfoChanged often fire together
                // on a track change. Coalesce them into one update.
                await Task.Delay(250, cts.Token).ConfigureAwait(false);
                await RefreshDefinitionAsync(WidgetIds.NowPlaying).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Superseded by a newer media event.
            }
            catch (Exception ex)
            {
                Log.Error("Media-triggered widget refresh failed.", ex);
            }
            finally
            {
                cts.Dispose();
            }
        });
    }

    private static void RunSafely(string operation, Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            Log.Error($"Widget provider callback '{operation}' failed.", ex);
        }
    }

    private readonly record struct WidgetSnapshot(string Id, string DefinitionId, WidgetSize Size);

    private sealed class WidgetState
    {
        public WidgetState(string id, string definitionId, WidgetSize size)
        {
            Id = id;
            DefinitionId = definitionId;
            Size = size;
        }

        public string Id { get; }

        public string DefinitionId { get; set; }

        public WidgetSize Size { get; set; }

        public string? LastTemplate { get; set; }
    }
}
