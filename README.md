# Eigene Widgets für Windows 11

Eine paketierte C#/.NET-10-App (WPF, Windows App SDK 2.6.4), die zwei eigene Widgets für das Windows-11-Widgets-Board und den Sperrbildschirm bereitstellt. Zusätzlich startet bei der Anmeldung ein schlanker Desktop-Host, der dieselben Karten direkt auf dem Desktop anzeigt und bedienbar macht:

| Widget | Größen | Inhalt |
| --- | --- | --- |
| **Jetzt läuft** | klein, mittel | Titel, Interpret, Album und Cover des systemweit laufenden Players über `GlobalSystemMediaTransportControlsSessionManager` |
| **System** | klein, mittel | CPU-Auslastung, Arbeitsspeicher und Akkustand |

Beide Widgets deklarieren ausdrücklich die **kleine** Größe. Damit sind sie für den Sperrbildschirm geeignet, sofern die installierte Windows-11-Version Sperrbildschirm-Widgets anbietet.

Der Desktop-Host ist ein randloses Tool-Fenster ohne Taskleisten- oder Alt-Tab-Eintrag. Er liegt auf der Desktop-Ebene: Bei sichtbarem Desktop wird er eingeblendet, sobald eine normale App den Vordergrund übernimmt, verschwindet er wieder. Es gibt weiterhin kein Live-Wallpaper, keinen Eingriff in die Wallpaper-Einstellungen und kein Reparenting in `Progman`/`WorkerW`.

---

## Voraussetzungen

- **Windows 11 x64**, Build 22621 (22H2) oder neuer.
  Sperrbildschirm-Widgets sind erst in neueren Windows-11-Builds verfügbar; ob der eigene Build sie anbietet, zeigt `Einstellungen → Personalisierung → Sperrbildschirm`.
- **Developer Mode** aktiviert:
  `Einstellungen → System → Für Entwickler → Entwicklermodus`.
  Er wird für die lokale MSIX-Installation und COM-Aktivierung benötigt.
- **Sideloading** erlaubt. Bei Windows 11 ist das standardmäßig der Fall.
- **.NET 10 SDK** (getestet mit 10.0.404) für den CLI-Build.
- **Visual Studio 2026** (oder neuer) mit Workload **„.NET-Desktopentwicklung“** – optional für Debugging und Deployment aus der IDE. Ältere Visual-Studio-Versionen können das .NET-10-Projekt nicht öffnen. Der Build über die mitgelieferten Skripte funktioniert auch ohne Visual Studio.
- **Windows App SDK 2.6.4** wird über NuGet eingebunden. Die App ist **self-contained** (inklusive .NET-Laufzeit, Windows-Desktop-Runtime für WPF und Windows-App-SDK-Runtime), daher ist keine separate Runtime-Installation nötig.
- **Windows SDK / MSIX Packaging Tools** sind für den Skript-Build nicht erforderlich: `makeappx.exe` und `signtool.exe` kommen aus dem NuGet-Paket `Microsoft.Windows.SDK.BuildTools`.
- Internetzugang beim ersten Build für die NuGet-Wiederherstellung.

> Die App wird nur für **x64** gebaut. Das Windows-11-Widgets-Board unterstützt keine x86-Anbieter.

---

## Projektstruktur

```text
CustomWidgets.sln
src/WidgetProvider/
  WidgetProvider.csproj        .NET-10-WPF-Projekt, self-contained, WinExe
  Package.appxmanifest         MSIX-Manifest: COM-Server, Widgets, StartupTask, Capabilities
  Program.cs                   Desktop-Host (ohne Argumente) oder COM-Server
  WidgetProvider.cs            IWidgetProvider-Implementierung und Widget-Lebenszyklus
  Cards.cs                     Adaptive-Card-JSON für beide Widgets und Größen
  Com/WidgetProviderFactory.cs COM-Klassenfabrik
  Desktop/
    DesktopHost.cs             STA-Thread und Single-Instance-Mutex
    DesktopHostWindow.xaml     Zwei Karten, Steuerung, Transparenz
    DesktopHostWindow.xaml.cs  Medien-/Systemdaten, Sichtbarkeit, Ziehen, Befehle
    DesktopHostSettings.cs     gespeicherte Fensterposition (JSON)
    DesktopLayer.cs            Tool-Fenster, Position und Z-Order
    VolumeController.cs        Master-Lautstärke über Core Audio (WASAPI)
  Services/MediaWatcher.cs     GSMTC-Ereignisse, Wiedergabebefehle, Cover
  Services/SystemMetricsService.cs  CPU/RAM/Akku auf Anforderung
  Assets/                      Logos, Widget-Icons, helle/dunkle Picker-Screenshots
scripts/
  GenerateAssets.ps1           erzeugt alle PNG-Assets neu
  Build.ps1                    publish + MSIX packen + signieren
  Install.ps1                  Dev-Zertifikat vertrauen + MSIX sideloaden
artifacts/                     Build-Ausgabe (nicht versioniert)
```

---

## Bauen

Im Projektordner:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\Build.ps1
```

Das Skript führt aus:

1. `dotnet publish` für `win-x64`, `--self-contained true`, `WindowsAppSDKSelfContained=true`
2. Kopieren des Publish-Ergebnisses in ein MSIX-Layout und Umbenennen von `Package.appxmanifest` nach `AppxManifest.xml`
3. `makeappx.exe pack`
4. Signieren mit einem selbstsignierten Entwicklungszertifikat (`CN=CustomWidgets Development`)

Ergebnis:

```text
artifacts/CustomWidgets.msix      signiertes Paket (ca. 115 MB, inkl. WPF-Desktop-Host)
artifacts/dev-cert.cer            öffentliches Dev-Zertifikat
artifacts/dev-cert.pfx            Dev-Zertifikat inkl. privatem Schlüssel (nicht weitergeben)
artifacts/layout/                 entpacktes MSIX-Layout
```

Nur kompilieren, ohne Paket:

```powershell
dotnet build src\WidgetProvider\WidgetProvider.csproj -c Release
```

Optionen des Build-Skripts:

```powershell
# ohne Signieren (nur zum Prüfen des Pakets)
.\scripts\Build.ps1 -SkipSign

# Debug-Build
.\scripts\Build.ps1 -Configuration Debug
```

Nach Änderungen am Manifest oder an Assets immer neu bauen und **neu installieren** – Windows cached Paketmetadaten.

### Assets neu erzeugen

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\GenerateAssets.ps1
```

Erzeugt Icons in 44/50/64/150 px sowie 300 × 304 px große Picker-Screenshots für helle und dunkle Darstellung. Die Screenshots entsprechen den Vorgaben des Widget-Pickers.

---

## Sideloading-Installation

### Empfohlen: Skript

PowerShell **als Administrator** öffnen:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\Install.ps1
```

Das Skript:

1. importiert `artifacts/dev-cert.cer` nach `Cert:\LocalMachine\TrustedPeople`,
2. prüft den Entwicklermodus und warnt gegebenenfalls,
3. installiert `artifacts/CustomWidgets.msix` mit `Add-AppxPackage`.

### Manuell

1. Zertifikat als Administrator vertrauen:

   ```powershell
   Import-Certificate -FilePath .\artifacts\dev-cert.cer `
       -CertStoreLocation Cert:\LocalMachine\TrustedPeople
   ```

2. Paket installieren:

   ```powershell
   Add-AppxPackage -Path .\artifacts\CustomWidgets.msix
   ```

   Alternativ nach dem Zertifikatsimport die `.msix`-Datei doppelklicken und **Installieren** wählen.

3. Installation prüfen:

   ```powershell
   Get-AppxPackage -Name CustomWidgets.WidgetProvider
   ```

> Das Entwicklungszertifikat ist nur für diesen Rechner gedacht. Für eine Weitergabe müsste ein eigenes Zertifikat verwendet und der `Publisher` im Manifest entsprechend angepasst werden. Eine Store-Veröffentlichung ist nicht vorgesehen.

---

## Desktop-Widgets

### Start und Autostart

Das Manifest registriert die StartupTask `CustomWidgetsDesktopHost` (`windows.startupTask`, `Enabled="true"`). Bei der Windows-Anmeldung startet Windows `CustomWidgets.WidgetProvider.exe` ohne Argumente; `Program.Main` startet dann den Desktop-Host. Der Widget-Anbieter wird weiterhin separat per COM aktiviert.

Nach der Installation ist die Task aktiviert (Registry `State=2`). Der Eintrag lässt sich im Task-Manager unter **Autostart** bzw. in den Windows-Einstellungen deaktivieren. Ein manueller Start ist normalerweise nicht nötig. Für Entwicklung und Tests:

```powershell
Start-Process explorer.exe -ArgumentList 'shell:AppsFolder\CustomWidgets.WidgetProvider_h12hzp5wv29n6!WidgetProvider'
```

Die Package Family ID kann je nach Publisher abweichen; `Get-AppxPackage -Name CustomWidgets.WidgetProvider | Select-Object PackageFamilyName` zeigt sie. Wegen `AppListEntry="none"` erscheint die App nicht im Startmenü, der AUMID-Start funktioniert trotzdem.

### Darstellung und Sichtbarkeit

- Zwei Karten, 820 × 300 DIP. Standardposition oben rechts mit 28 DIP Abstand; die zuletzt gezogene Position wird gespeichert.
- Ziehen an einer freien Kartenfläche (nicht auf den Buttons) verschiebt beide Karten. Der Griff ⠿ oben rechts an jeder Karte markiert die Fläche. Die Position wird in `%LOCALAPPDATA%\CustomWidgets\desktop-host.json` gespeichert und beim Start wiederhergestellt.
- Nahe der horizontalen Bildschirmmitte rastet der Host ein (Toleranz 18 DIP), ähnlich einer PowerPoint-Ausrichtungslinie. Die vertikale Position bleibt frei; der Host lässt sich anschließend entlang der Mittellinie verschieben. Das Einrasten bezieht sich auf die Arbeitsfläche des Hauptbildschirms.
- Randlos, transparent, kein Taskleisten- oder Alt-Tab-Eintrag, `WS_EX_NOACTIVATE`: Klicks steuern die Widgets, ohne den Tastaturfokus zu übernehmen.
- Sichtbar nur, wenn der Desktop den Vordergrund bildet (`Progman`, `WorkerW`, Taskleiste oder Shell). Bei jeder normalen App wird das Fenster versteckt und der System-Timer angehalten; die Prüfung läuft alle 250 ms.
- Bei sichtbarem Desktop liegt das Fenster über der Desktopfläche. Sobald eine App in den Vordergrund kommt, wird es versteckt – es bleibt also nicht dauerhaft über Anwendungsfenstern.
- Kein `WorkerW`-Kind und kein Wallpaper-Eingriff; die Desktop-Ebene wird über ein eigenes Tool-Fenster und Z-Order hergestellt.
- Der Host kann Desktop-Symbole überdecken. Die Position wird beim Start aus `%LOCALAPPDATA%\CustomWidgets\desktop-host.json` geladen und auf den sichtbaren virtuellen Bildschirm begrenzt. Zum Zurücksetzen die Datei löschen; dann erscheint der Host wieder oben rechts.

### Bedienung

| Karte | Steuerung |
| --- | --- |
| **Jetzt läuft** | Zurück, Play/Pause, Weiter; Cover, Titel, Interpret, Album, Wiedergabestatus sowie Lautstärkeregler mit Prozentwert, aktivem Ausgabegerät und Stummschalten |
| **System** | Aktualisieren; CPU, RAM und Akku; aktualisiert beim Einblenden und danach alle 15 s |

Der Lautstärkebalken liest und setzt den echten Windows-Master-Pegel über die Core-Audio-Schnittstelle `IAudioEndpointVolume`; das Lautsprecher-Symbol schaltet stumm. Die Medienbefehle laufen über die GSMTC-Sitzung. Beides funktioniert auch im paketierten Host. Zum Verschieben an einer freien Kartenfläche ziehen; die Buttons und der Regler bleiben bedienbar.

### Ressourcen

Der Host läuft als eine Instanz pro Sitzung (Mutex `Local\CustomWidgets.DesktopHost`). Medienänderungen sind ereignisgesteuert. Systemwerte werden nur bei sichtbarem Desktop alle 15 s gemessen; der Lautstärkebalken wird bei sichtbarem Desktop einmal pro Sekunde mit dem Core-Audio-Endpunkt abgeglichen. Wechselt das Standard-Ausgabegerät, wird der Endpunkt automatisch neu gebunden. Bei verdecktem Desktop pausieren beide Timer.

---

## Widgets hinzufügen

### Desktop-Widgets-Board

1. **Win + W** drücken (oder auf das Widgets-Symbol in der Taskleiste klicken).
2. Oben im Picker **„Widgets hinzufügen“** bzw. das **+** wählen.
3. Die App **„Eigene Widgets“** und darin **„Jetzt läuft“** oder **„System“** auswählen.
4. Das Widget erscheint auf dem Board. Über das Kontextmenü lässt sich zwischen **klein** und **mittel** wechseln.

Beim ersten Öffnen kann es einige Sekunden dauern, bis Windows den neuen Anbieter enumeriert hat. Widgets-Board schließen und erneut mit **Win + W** öffnen hilft.

### Sperrbildschirm

1. **Einstellungen → Personalisierung → Sperrbildschirm** öffnen.
2. Im Abschnitt **„Widgets“** (bzw. „Sperrbildschirm-Widgets“) **Widget hinzufügen** wählen.
3. **„Jetzt läuft“** oder **„System“** auswählen.

Voraussetzungen dafür:

- Die installierte Windows-11-Version unterstützt Sperrbildschirm-Widgets. Fehlt der Abschnitt in den Einstellungen, bietet der Build diese Funktion nicht an – unabhängig von dieser App.
- Das Widget unterstützt die kleine Größe. Das ist im Manifest für beide Widgets deklariert (`<Size Name="small" />`).
- Sperrbildschirm-Widgets werden nicht auf jedem Gerät/Region angeboten; die Verfügbarkeit steuert Windows.

---

## Verhalten und technische Hinweise

### Zwei Betriebsarten

Die App ist eine **MSIX-paketierte Win32-App** (`Windows.FullTrustApplication`) mit `OutputType=WinExe`. Das Manifest registriert:

- einen Out-of-Process-COM-Server (`com:ExeServer`, Argument `-RegisterProcessAsComServer`) mit CLSID `59D3A472-B0E7-45FC-865B-68863B7D3DC3`,
- die Widget-Erweiterung `uap3:AppExtension Name="com.microsoft.windows.widgets"` mit `CreateInstance` auf dieselbe CLSID,
- zwei `Definition`-Einträge mit den IDs `NowPlayingWidget` und `SystemWidget`,
- die StartupTask `CustomWidgetsDesktopHost`, die dieselbe EXE ohne Argumente startet.

`Program.Main` verzweigt entsprechend:

- **ohne Argumente** (StartupTask, manueller Start) → WPF-Desktop-Host auf einem STA-Thread; eine Instanz pro Sitzung.
- **`-RegisterProcessAsComServer`** bzw. **`-Embedding`** → COM-Klassenfabrik über `CoRegisterClassObject`; das Widgets-Board ruft danach `IWidgetProvider`-Callbacks auf.

Der COM-Prozess bleibt am Leben, solange Widgets angeheftet sind, und beendet sich, wenn das letzte Widget entfernt wurde (bzw. nach zwei Minuten ohne Widgets). Der Desktop-Host läuft separat und hat ein Tool-Fenster, aber keinen Taskleisten- oder Alt-Tab-Eintrag. Beide Prozesse können gleichzeitig laufen.

### „Jetzt läuft“

- Verwendet `GlobalSystemMediaTransportControlsSessionManager.RequestAsync()` und die aktuelle Sitzung.
- Reagiert auf `CurrentSessionChanged`, `MediaPropertiesChanged` und `PlaybackInfoChanged` – **ereignisgesteuert, kein Sekunden-Polling**.
- Mehrfach ausgelöste Ereignisse beim Titelwechsel werden über eine kurze 250-ms-Entprellung zusammengefasst.
- Das Cover wird auf maximal 144 px skaliert, als PNG kodiert und als Base64-Data-URI in die Adaptive Card eingebettet. Dadurch braucht das Widget keinen Webserver und keine externe Bild-URL.
- Ohne laufende Sitzung erscheint ein sauberer Leerzustand („Keine Wiedergabe“).
- Fehlende Berechtigung (`globalMediaControl` fehlt/entzogen) und Lesefehler werden abgefangen und als eigener Kartenzustand angezeigt.

### „System“

- CPU: `GetSystemTimes` mit zwei Messpunkten im Abstand von 500 ms; die Differenz wird nur dann erhoben, wenn das Widget aktualisiert wird.
- RAM: `GlobalMemoryStatusEx`, Anzeige von belegtem und gesamtem Arbeitsspeicher.
- Akku: `GetSystemPowerStatus` inklusive Ladezustand, Netzbetrieb und „Kein Akku“.
- Aktualisierung beim Anheften, beim Öffnen/Aktivieren des Boards und bei einer Größenänderung. **Kein Dauer-Timer und kein Hintergrund-Polling.**
- Werte werden deutsch formatiert (z. B. `19,5 von 32,0 GB`).

### Lautstärke

- Der Balken liest und setzt den Windows-Master-Pegel über `IAudioEndpointVolume` (Core Audio/WASAPI), nicht über emulierte Lautstärketasten.
- Der Regler reicht von 0 bis 100 %; der aktuelle Prozentwert steht direkt neben dem Balken. Darunter steht der Anzeigename des aktiven Standard-Ausgabegeräts; er wird bei einem Gerätewechsel automatisch aktualisiert. Das Lautsprecher-Symbol schaltet stumm; Pegel und Mute-Zustand kommen direkt vom Audio-Endpunkt.
- Der Anzeigename stammt aus `PKEY_Device_FriendlyName` des aktiven Endpunkts. Fehlt er, zeigt der Host `Ausgabe: unbekannt`; bei fehlender Audioausgabe steht dort `Ausgabe: nicht verfügbar`.
- Bei aktiver Stummschaltung erscheint direkt neben dem Prozentwert zusätzlich `Stumm`; beim Aufheben verschwindet das Label wieder.
- Bei sichtbarem Desktop wird der Pegel einmal pro Sekunde abgeglichen, damit auch Änderungen über Tastatur oder Systemmischer im Balken ankommen. Dabei wird auch das aktuelle Standard-Ausgabegerät geprüft; wechselt es, wird der Endpunkt neu gebunden und der Balken zeigt den Pegel des neuen Geräts. Bei verdecktem Desktop pausiert der Timer.
- Ist kein Audio-Endpunkt verfügbar, werden Regler und Stummschaltung deaktiviert und der Fehler protokolliert.

### Darstellung hell/dunkel

Die Adaptive Cards verwenden ausschließlich vom Host eingefärbte Standardtexte und `isSubtle`; dadurch passen sie sich automatisch an das helle bzw. dunkle Systemdesign an. Für den Widget-Picker liefert das Manifest getrennte Screenshots für `DarkMode` und `LightMode`. Der Desktop-Host verwendet bewusst dunkle Karten mit heller Schrift, unabhängig vom Systemdesign; die Board-Widgets bleiben themenabhängig.

### Fehlerfälle

- **Keine Media-Session:** Leerzustand, keine Exception nach außen.
- **Fehlende Berechtigung:** eigener Kartenzustand mit Hinweis auf die Berechtigung.
- **Update auf ein deaktiviertes/entferntes Widget:** `WidgetManager.UpdateWidget` wird in `try/catch` ausgeführt und der Fehler protokolliert; der Anbieter läuft weiter.
- **COM-/Callback-Fehler:** jeder Callback ist gekapselt; unbehandelte Ausnahmen werden zusätzlich über `AppDomain.UnhandledException` und `TaskScheduler.UnobservedTaskException` protokolliert.

### Capabilities

```xml
<rescap:Capability Name="runFullTrust" />
<uap7:Capability Name="globalMediaControl" />
```

`globalMediaControl` ist eine **eingeschränkte Capability**. Für Sideloading ist keine Microsoft-Freigabe nötig; bei einer Store-Veröffentlichung müsste sie begründet und genehmigt werden. Die App fordert keine Netzwerk-, Kamera- oder Dateisystem-Capabilities an.

---

## Fehlerbehebung

### Widget erscheint nicht im Picker

1. Prüfen, ob das Paket installiert ist:

   ```powershell
   Get-AppxPackage -Name CustomWidgets.WidgetProvider
   ```

2. Entwicklermodus aktiv? (`Einstellungen → System → Für Entwickler`)
3. Nach Manifest-/Asset-Änderungen neu bauen **und neu installieren**:
   ```powershell
   .\scripts\Build.ps1
   .\scripts\Install.ps1   # als Administrator
   ```
4. Alle im Manifest referenzierten Dateien müssen im Paket liegen. Das Build-Skript kopiert `Assets\**\*.png` automatisch. Namen und Pfade in `Package.appxmanifest` prüfen.
5. Widgets-Board schließen und erneut öffnen; notfalls Explorer neu starten.
6. Windows-Ereignisanzeige → `Anwendungs- und Dienstprotokolle → Microsoft → Windows → AppxDeployment-Server` auf Fehler prüfen.
7. Log der App ansehen (siehe unten).

### Desktop-Widgets erscheinen nicht

- Der Host wird nur bei sichtbarem Desktop angezeigt. Mit **Win + D** oder über die Taskleiste zum Desktop wechseln; bei geöffneten Apps ist das Ausblenden beabsichtigt.
- Prüfen, ob der Host-Prozess läuft:

  ```powershell
  Get-Process CustomWidgets.WidgetProvider -ErrorAction SilentlyContinue
  ```

- Autostart prüfen: Task-Manager → **Autostart** bzw. Registry `HKCU\Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\SystemAppData\CustomWidgets.WidgetProvider_h12hzp5wv29n6\CustomWidgetsDesktopHost` (Wert `State` sollte `2` sein).
- Manuell starten:

  ```powershell
  Start-Process explorer.exe -ArgumentList 'shell:AppsFolder\CustomWidgets.WidgetProvider_h12hzp5wv29n6!WidgetProvider'
  ```

- Bei anderer Auflösung oder Multimonitor wird die gespeicherte Position beim Start auf den sichtbaren virtuellen Bildschirm begrenzt. Zum Zurücksetzen `%LOCALAPPDATA%\CustomWidgets\desktop-host.json` löschen.
- Log unter `%LOCALAPPDATA%\CustomWidgets\logs\provider.log` auf `Desktop host window loaded` und Fehler prüfen.

### Widget bleibt leer oder zeigt keine Werte

- Log unter `%LOCALAPPDATA%\CustomWidgets\logs\provider.log` prüfen. Bei paketierter Ausführung kann Windows den Pfad in den Paket-LocalCache umleiten: `%LOCALAPPDATA%\Packages\CustomWidgets.WidgetProvider_*\LocalCache\Local\CustomWidgets\logs\provider.log`.
- Bei „Jetzt läuft“: einen Player starten, der SystemMediaTransportControls nutzt (z. B. Spotify, Windows Media Player, Edge). Manche Player melden keine Metadaten.
- Bei „System“: Nach dem Öffnen des Boards kurz warten; die CPU-Messung benötigt 500 ms.
- Fehler `0x80070005` im Log deutet auf eine fehlende/entzogene Medienberechtigung hin.

### Kein Zugriff auf die Medienwiedergabe

- Sicherstellen, dass das Paket mit dem aktuellen Manifest installiert wurde (Capability `globalMediaControl`).
- Paket deinstallieren und neu installieren, damit die Capability neu ausgewertet wird.
- Der Player muss eine GSMTC-Sitzung bereitstellen. Reine Browser-Player ohne Media-Session-Unterstützung funktionieren nicht.

### Update wird abgelehnt

Meldungen wie „widget may be disabled“ sind erwartbar, wenn das Widget gerade entfernt oder vom Host deaktiviert wurde. Der Anbieter protokolliert das und bleibt aktiv. Das Widget wird beim nächsten Anheften/Aktivieren erneut aktualisiert.

### Zertifikat abgelaufen oder ungültig

- Das Dev-Zertifikat ist drei Jahre gültig. Bei Ablauf: altes Zertifikat mit Betreff `CN=CustomWidgets Development` unter `Cert:\CurrentUser\My` entfernen, `Build.ps1` erneut ausführen und `Install.ps1` als Administrator starten.
- `0x800B0109` bzw. „Zertifikat nicht vertrauenswürdig“ bedeutet, dass `dev-cert.cer` nicht in `LocalMachine\TrustedPeople` liegt.

### Deinstallieren

```powershell
Get-AppxPackage -Name CustomWidgets.WidgetProvider | Remove-AppxPackage
```

Optional das Dev-Zertifikat entfernen:

```powershell
Get-ChildItem Cert:\LocalMachine\TrustedPeople |
    Where-Object { $_.Subject -eq 'CN=CustomWidgets Development' } |
    Remove-Item
```

---

## Grenzen dieses Projekts

- **Keine Store-Veröffentlichung:** Das Manifest deklariert eine eingeschränkte Capability und nutzt ein selbstsigniertes Entwicklungszertifikat. Beides ist für Sideloading vorgesehen, nicht für den Store.
- **Sperrbildschirm-Widgets sind OS-abhängig.** Die App liefert die technische Voraussetzung (kleine Größe), aber Windows entscheidet, ob und wo Sperrbildschirm-Widgets angeboten werden.
- **Nur x64.** Das Widgets-Board unterstützt keine x86-Anbieter.
- **Keine Hintergrundaktualisierung bei geschlossenem Board.** Medienänderungen werden nur verarbeitet, solange der Anbieterprozess aktiv ist; das Board aktiviert ihn bei Bedarf erneut. Das ist bewusst ressourcenschonend. Der Desktop-Host ist davon unabhängig und läuft ab der Anmeldung.
- **Desktop-Host kann Desktop-Symbole überdecken.** Die Position ist frei wählbar und wird gespeichert; beim Start wird sie auf den sichtbaren Bereich begrenzt.
- **Laufzeit geprüft (13.11.2026, Windows 11 Build 26300, Paket 1.2.7.0):** Das signierte MSIX wurde aktualisiert (Status `Ok`), die Definitionen für „Jetzt läuft“ und „System“ sind weiterhin im Paket enthalten. Der paketierte Desktop-Host zeigte auf dem Desktop Cover, Titel, Interpret sowie CPU, RAM und Akku. Play/Pause funktioniert. Der Lautstärkebalken las beim Start 50 %, setzte per Klick 78 % und 96 % und per Ziehen schrittweise 98 %; der Prozentwert neben dem Balken wurde bei 12 % und 96 % geprüft. Unter dem Regler stand der aktive Ausgabename **Lautsprecher (Realtek(R) Audio)**. Beim Stummschalten erschien neben dem Prozentwert zusätzlich **Stumm**, beim Aufheben verschwand das Label. Beim Wechsel des Standard-Ausgabegeräts auf **Realtek Digital Output (Realtek(R) Audio)** band der Host automatisch neu, zeigte 26 % und aktualisierte den Namen; nach dem Zurückwechseln auf **Lautsprecher** stand der Regler wieder auf 12 %. Bei fokussiertem Freebuff wurde der Host ausgeblendet und beim Zurückkehren auf den Desktop wieder eingeblendet. Beim Ziehen rastete der Host nahe der Mittellinie auf **550, 400** ein; anschließend ließ er sich vertikal verschieben, ohne die Mittellinie zu verlassen. Die zuletzt gespeicherte Position wurde beim Start wiederhergestellt. Die StartupTask ist aktiviert (`State=2`). Nicht geprüft: Darstellung auf einem tatsächlich gesperrten Bildschirm, Autostart über einen echten Neustart/Abmelden, andere Auflösungen/Multimonitor, Langzeitverbrauch sowie das erneute Öffnen der Board-/Sperrbildschirm-Auswahl nach dem Update.

---

## Technische Versionen

| Komponente | Version |
| --- | --- |
| App-Paket | 1.2.8.0 |
| Ziel-Framework | `net10.0-windows10.0.22621.0` |
| Windows App SDK | 2.6.4 (self-contained) |
| .NET SDK | 10.0.404 |
| MSIX-Buildtools | `Microsoft.Windows.SDK.BuildTools` 10.0.28000.2957 |
| Minimale Windows-Version | 10.0.22621.0 |
| Architektur | x64 |

Zum Aktualisieren des Windows App SDK die `PackageReference` in `WidgetProvider.csproj` anpassen und neu bauen. Die Widget-APIs sind seit 1.6 stabil; das Projekt nutzt die aktuelle 2.x-Linie.
