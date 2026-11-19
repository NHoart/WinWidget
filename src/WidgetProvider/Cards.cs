using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using CustomWidgets.WidgetProvider.Services;
using Microsoft.Windows.Widgets;
using Microsoft.Windows.Widgets.Providers;

namespace CustomWidgets.WidgetProvider;

/// <summary>
/// Builds the Adaptive Card payloads. Cards intentionally use only host-theme
/// colors (default text plus isSubtle) so light and dark mode both render
/// correctly without a separate template per theme.
/// </summary>
internal static class Cards
{
    internal const string OpenSourceAppVerb = "openSourceApp";

    private static readonly CultureInfo German = CultureInfo.GetCultureInfo("de-DE");

    private static readonly object OpenSourceAppAction = new
    {
        type = "Action.Execute",
        title = "App öffnen",
        verb = OpenSourceAppVerb
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static string NowPlaying(MediaSnapshot media, WidgetSize size)
    {
        bool small = size == WidgetSize.Small;

        if (media.Status != MediaStatus.Available)
        {
            return BuildEmptyNowPlaying(media, small);
        }

        string title = Clip(media.Title, 120);
        string artist = Clip(media.Artist, 100);
        string album = Clip(media.Album, 100);

        var body = new List<object>();

        if (small)
        {
            var titleBlock = new
            {
                type = "TextBlock",
                text = title,
                weight = "bolder",
                size = "medium",
                wrap = true,
                maxLines = 2,
                spacing = "none"
            };

            var artistBlock = new
            {
                type = "TextBlock",
                text = artist,
                size = "small",
                isSubtle = true,
                wrap = true,
                maxLines = 1,
                spacing = "none"
            };

            if (!string.IsNullOrEmpty(media.ArtDataUri))
            {
                body.Add(new
                {
                    type = "ColumnSet",
                    columns = new object[]
                    {
                        new
                        {
                            type = "Column",
                            width = "auto",
                            verticalContentAlignment = "center",
                            items = new object[]
                            {
                                new
                                {
                                    type = "Image",
                                    url = media.ArtDataUri,
                                    altText = "Albumcover",
                                    size = "small"
                                }
                            }
                        },
                        new
                        {
                            type = "Column",
                            width = "stretch",
                            spacing = "medium",
                            verticalContentAlignment = "center",
                            items = new object[] { titleBlock, artistBlock }
                        }
                    }
                });
            }
            else
            {
                body.Add(titleBlock);
                body.Add(artistBlock);
            }
        }
        else
        {
            var textItems = new List<object>
            {
                new
                {
                    type = "TextBlock",
                    text = "JETZT LÄUFT",
                    size = "small",
                    weight = "bolder",
                    isSubtle = true,
                    spacing = "none"
                },
                new
                {
                    type = "TextBlock",
                    text = title,
                    size = "large",
                    weight = "bolder",
                    wrap = true,
                    maxLines = 2,
                    spacing = "small"
                },
                new
                {
                    type = "TextBlock",
                    text = artist,
                    wrap = true,
                    maxLines = 1,
                    spacing = "none"
                }
            };

            if (!string.IsNullOrWhiteSpace(album))
            {
                textItems.Add(new
                {
                    type = "TextBlock",
                    text = album,
                    size = "small",
                    isSubtle = true,
                    wrap = true,
                    maxLines = 1,
                    spacing = "none"
                });
            }

            if (media.IsPaused)
            {
                textItems.Add(new
                {
                    type = "TextBlock",
                    text = "Pausiert",
                    size = "small",
                    isSubtle = true,
                    spacing = "small"
                });
            }

            if (!string.IsNullOrEmpty(media.ArtDataUri))
            {
                body.Add(new
                {
                    type = "ColumnSet",
                    columns = new object[]
                    {
                        new
                        {
                            type = "Column",
                            width = "auto",
                            verticalContentAlignment = "top",
                            items = new object[]
                            {
                                new
                                {
                                    type = "Image",
                                    url = media.ArtDataUri,
                                    altText = "Albumcover",
                                    size = "medium"
                                }
                            }
                        },
                        new
                        {
                            type = "Column",
                            width = "stretch",
                            spacing = "medium",
                            verticalContentAlignment = "center",
                            items = textItems.ToArray()
                        }
                    }
                });
            }
            else
            {
                body.AddRange(textItems);
            }
        }

        object? selectAction = string.IsNullOrWhiteSpace(media.SourceAppUserModelId)
            ? null
            : OpenSourceAppAction;

        return Serialize(new CardDocument { Body = body, SelectAction = selectAction });
    }

    public static string SystemWidget(SystemSnapshot snapshot, WidgetSize size)
    {
        bool small = size == WidgetSize.Small;

        string cpu = Percent(snapshot.CpuPercent);
        string ram = Percent(snapshot.MemoryPercent);
        string battery = snapshot.BatteryPercent is int batteryPercent
            ? batteryPercent.ToString(German) + " %"
            : "–";

        string memoryDetail = snapshot.MemoryTotalBytes > 0
            ? $"{Gigabytes(snapshot.MemoryUsedBytes)} von {Gigabytes(snapshot.MemoryTotalBytes)} GB"
            : "Nicht verfügbar";

        string batteryDetail = snapshot.Battery switch
        {
            BatteryState.Charging => "Lädt",
            BatteryState.OnAc => "Netzbetrieb",
            BatteryState.OnBattery => "Akkubetrieb",
            BatteryState.NoBattery => "Kein Akku",
            _ => "Nicht verfügbar"
        };

        var columns = new object[]
        {
            Stat(cpu, "CPU", small ? null : $"{snapshot.LogicalProcessors} logische Kerne"),
            Stat(ram, "Arbeitsspeicher", small ? null : memoryDetail),
            Stat(battery, "Akku", small ? null : batteryDetail)
        };

        var body = new List<object>();

        if (small)
        {
            body.Add(new
            {
                type = "TextBlock",
                text = "SYSTEM",
                size = "small",
                weight = "bolder",
                isSubtle = true,
                spacing = "none"
            });
        }
        else
        {
            body.Add(new
            {
                type = "TextBlock",
                text = "System",
                size = "medium",
                weight = "bolder",
                spacing = "none"
            });
            body.Add(new
            {
                type = "TextBlock",
                text = $"Aktualisiert um {snapshot.UpdatedAt.ToLocalTime().ToString("HH:mm", German)}",
                size = "small",
                isSubtle = true,
                spacing = "none"
            });
        }

        body.Add(new
        {
            type = "ColumnSet",
            spacing = "medium",
            columns
        });

        return Serialize(new CardDocument { Body = body });
    }

    private static object Stat(string value, string label, string? detail)
    {
        var items = new List<object>
        {
            new
            {
                type = "TextBlock",
                text = value,
                size = "large",
                weight = "bolder",
                wrap = false,
                spacing = "none"
            },
            new
            {
                type = "TextBlock",
                text = label,
                size = "small",
                isSubtle = true,
                wrap = true,
                spacing = "none"
            }
        };

        if (!string.IsNullOrEmpty(detail))
        {
            items.Add(new
            {
                type = "TextBlock",
                text = detail,
                size = "small",
                isSubtle = true,
                wrap = true,
                spacing = "none"
            });
        }

        return new
        {
            type = "Column",
            width = "stretch",
            spacing = "none",
            items = items.ToArray()
        };
    }

    private static string BuildEmptyNowPlaying(MediaSnapshot media, bool small)
    {
        (string title, string message) = media.Status switch
        {
            MediaStatus.AccessDenied => (
                "Medienzugriff verweigert",
                "Die Berechtigung „Globale Mediensteuerung“ fehlt oder wurde entzogen."),
            MediaStatus.Error => (
                "Medien nicht verfügbar",
                media.ErrorMessage ?? "Die Wiedergabeinformationen konnten nicht gelesen werden."),
            _ => (
                "Keine Wiedergabe",
                "Starten Sie einen Player, um Titel, Interpret und Cover zu sehen.")
        };

        var body = new List<object>();

        if (small)
        {
            body.Add(new
            {
                type = "TextBlock",
                text = "♪",
                size = "extraLarge",
                isSubtle = true,
                spacing = "none"
            });
            body.Add(new
            {
                type = "TextBlock",
                text = title,
                weight = "bolder",
                wrap = true,
                maxLines = 2,
                spacing = "small"
            });
            body.Add(new
            {
                type = "TextBlock",
                text = media.Status == MediaStatus.NoSession ? "Player starten" : "Berechtigung prüfen",
                size = "small",
                isSubtle = true,
                wrap = true,
                spacing = "none"
            });
        }
        else
        {
            body.Add(new
            {
                type = "TextBlock",
                text = "JETZT LÄUFT",
                size = "small",
                weight = "bolder",
                isSubtle = true,
                spacing = "none"
            });
            body.Add(new
            {
                type = "TextBlock",
                text = title,
                size = "large",
                weight = "bolder",
                wrap = true,
                spacing = "small"
            });
            body.Add(new
            {
                type = "TextBlock",
                text = message,
                size = "small",
                isSubtle = true,
                wrap = true,
                maxLines = 2,
                spacing = "none"
            });
        }

        return Serialize(new CardDocument { Body = body });
    }

    private static string Percent(double? value) =>
        value is null ? "–" : value.Value.ToString("0", German) + " %";

    private static string Gigabytes(ulong bytes) =>
        (bytes / (1024.0 * 1024.0 * 1024.0)).ToString("0.0", German);

    private static string Clip(string value, int maxLength)
    {
        string trimmed = value.Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..(maxLength - 1)].TrimEnd() + "…";
    }

    private static string Serialize(object card) => JsonSerializer.Serialize(card, JsonOptions);

    private sealed class CardDocument
    {
        [JsonPropertyName("type")]
        public string Type => "AdaptiveCard";

        [JsonPropertyName("$schema")]
        public string Schema => "http://adaptivecards.io/schemas/adaptive-card.json";

        [JsonPropertyName("version")]
        public string Version => "1.5";

        [JsonPropertyName("body")]
        public IReadOnlyList<object> Body { get; init; } = Array.Empty<object>();

        [JsonPropertyName("selectAction")]
        public object? SelectAction { get; init; }
    }
}
