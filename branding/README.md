# ThixShot – Logo

Ein aufgenommenes Bild (Landschaft mit Sonne) in den weißen Eckmarken einer Bildschirmauswahl, auf einer blauen Kachel –
im Stil aller Thix-Apps (Kachel mit Rand, weißer Gegenstand, Akzent in der Farbe der App). Akzentfarbe **#2F6FED**,
dunkles Blau **#163E9E**, Verlauf der Kachel **#5A9CFF → #1E5BE6**, Sonne **#FFC857**.

| Datei | Detailstufe | Einsatz |
|---|---|---|
| `logo-icon.svg` | voll (ab 48 px): Bild mit Sonne und zwei Bergen, vier Eckmarken | App-Icon (Windows .ico ab 48 px, macOS .icns), Fenster, Titelleiste |
| `logo-small.svg` | reduziert (16–32 px): ein weißes Bild mit einem Berg, dicke Eckmarken, Kachel randlos | kleine .ico-Größen, Taskleiste |
| `logo-full.svg` | Icon + Wortmarke „Thix**Shot**“ | README, Kopfzeilen |

`png/` enthält alle gerenderten Größen. Neu erzeugen (nach jeder Änderung an einer SVG):

```
cd tools && npm install && node render-icons.js
```

Das Skript rendert mit dem installierten Microsoft Edge (headless) und schreibt zusätzlich `src/ThixShot/Assets/ThixShot.ico`
(Programm, Fenster, MSI), `ThixShot.png` (1024 px mit Apples Rand; `scripts/package-macos.sh` baut daraus das `.icns`) und
`Mark.png` (Logo in den Titelleisten und der Bereitschaftsleiste).
Das Icon liegt außerdem im ThixLauncher (`src/ThixLauncher/Assets/apps/thixshot.png` = `png/thixshot-256.png`).
