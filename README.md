# ThixShot

ThixShot ist das Screenshot- und Aufnahmestudio für Windows und macOS von ThixApps.de. Screenshots lassen sich direkt zuschneiden und markieren. Bei Bildschirmvideos folgt ein optionaler animierter Zoom den Mausklicks.

![ThixShot Aufnahmestudio](docs/aufnahmestudio.png)

## Installation

Die [Releases](https://github.com/Real-TTX/ThixShot/releases/latest) enthalten ein Windows-MSI für x64 sowie getrennte DMGs für Macs mit Apple Silicon und Intel. Unter Windows das MSI öffnen. Auf macOS die passende DMG öffnen und ThixShot nach „Programme“ ziehen. Die App bringt die .NET-Laufzeit mit.

Die Bildschirmaufnahme nutzt unter Windows 10/11 Media Foundation. Windows-N-Editionen benötigen das Media Feature Pack. Auf macOS 14 oder neuer benötigt ThixShot die Freigabe für Bildschirm- und Systemaudioaufnahme. Die Mac-Pakete tragen eine Ad-hoc-Signatur. Apple-Notarisierung und Windows-Publishersignatur stehen aus.

## Aufnahme und Mediathek

- **Screenshot** erfasst den Hauptbildschirm oder einen mit der Maus gewählten Ausschnitt. Der Timer bietet 3, 5 oder 10 Sekunden Vorlauf. Im Editor stehen Textmarker, Pfeile, Rahmen, Zuschneiden und Rückgängig zur Verfügung. „In Mediathek speichern“ legt eine neue PNG-Datei an. Das Vorschaubild im Studio lässt sich ebenfalls im Editor ausprobieren.
- **Video** zeichnet den Hauptbildschirm oder einen Ausschnitt mit bis zu 30 Bildern pro Sekunde in MP4/H.264 auf. Mauszeiger, Klick-Zoom und Systemton lassen sich einzeln einstellen. Das schwebende Bedienfeld zeigt die Dauer und beendet die Aufnahme. Die Ausgabe passt in 1920 × 1080 Pixel und erhält das Seitenverhältnis.
- **Mediathek** bietet Suche, Formatfilter und Sortierung. Aufnahmedetails öffnen, um Dateien umzubenennen, zu exportieren, zu löschen oder hochzuladen. Screenshots lassen sich in die Zwischenablage kopieren, Videos im Standardplayer abspielen. Die Dateien liegen im lokalen Anwendungsdatenordner unter `ThixShot/Media`.
- **Einstellungen** enthalten die Darstellung (System, Hell oder Dunkel), Autostart und Updates. Vollautomatische Updates sind optional. Titelleiste, einklappbare Seitenleiste und Dialoge verwenden wie ThixClip die gemeinsame MatDesktop-Oberfläche. Die Versionsnummer unten links öffnet „Über ThixShot“.
- **ThixCloud** koppelt das Gerät per Browsercode mit der eigenen Instanz. Manuell ausgewählte Dateien landen im persönlichen ThixDrive im Ordner **ThixShot**. Für MatCloud-Vertrag 1.4.0 liegt die erforderliche [Serverfreigabe für die App-ID shot](integrations/matcloud/README.md) bei.

## Entwicklung

Benötigt werden .NET SDK 10 und auf macOS die Xcode Command Line Tools. Die Oberfläche verwendet Avalonia UI. Die macOS-Aufnahme basiert auf ScreenCaptureKit und AVFoundation, Windows verwendet GDI, Media Foundation und WASAPI.

```sh
dotnet build ThixShot.slnx
dotnet test tests/ThixShot.Tests
dotnet run --project src/ThixShot
```

Das Windows-MSI entsteht mit `./scripts/package-windows.ps1`. Auf einem Mac erstellt `bash scripts/package-macos.sh osx-arm64` die Apple-Silicon-Pakete, `osx-x64` die Intel-Pakete. Die Ausgaben liegen in `artifacts/packages/`.

| Build | Anzeige | Verteilung |
| --- | --- | --- |
| main | `0.1.<Laufnummer>-<yyyyMMdd>` | GitHub Release und Release-Updatekanal |
| dev | `nightly-<Laufnummer>-<yyyyMMdd>` | Nightly-Updatekanal |
| Weitere Branches, Pull Requests, lokaler Build | `local-<yyyyMMdd>` | CI-Artefakte bzw. lokales Paket |

GitHub Actions prüft Oberfläche und Bildbearbeitung, testet die nativen Videoencoder mit synthetischem Material und baut die drei Installer. Die beiden Updatekanäle liefern jeweils ein Manifest mit Buildnummer und SHA-256-Prüfsummen.
