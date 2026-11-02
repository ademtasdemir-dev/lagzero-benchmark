# LAG|ZERØ Benchmark

**TESTED. MEASURED. OPTIMIZED.**

[Deutsch](#deutsch) · [English](#english)

---

## Deutsch

LAGZERO Benchmark ist ein Windows-Programm (eine einzige `.exe`, kein Installer), das deinen PC misst und die Ergebnisse in einfachem Deutsch erklärt – nur mit gemessenen Werten, ohne Online-Vergleichsdatenbank.

### Tests

| Reiter | Was gemessen wird |
|---|---|
| **System** | CPU, GPU, RAM (Menge, Takt, Module), Mainboard, Laufwerke, Hauptmonitor (Auflösung, Hz), Windows-Version (WMI/Registry) |
| **CPU** | Ganzzahl, Fließkomma und SIMD (AVX2/FMA, ohne FMA automatisch Ersatzweg) – Einzelkern und Mehrkern, Punktzahl und Dauer |
| **RAM** | Lesen, Schreiben, Kopieren (MB/s) und Zugriffslatenz (ns); Hinweise zu freiem/reserviertem RAM, Steckplätzen und Takt |
| **GPU** | Direct3D-11-Rendertest (FPS, Frametime) in wählbarer Auflösung und Compute-Test (FP32-TFLOPS) |
| **Frametime** | eigenes Testfenster, 10 s: FPS Ø, 1 % / 0,1 % Low, Ruckler, Verlaufsgrafik, optional VSync |
| **Laufwerke & USB** | sequenziell 1 MiB und 4K zufällig (QD1/QD32) mit ungepuffertem I/O; Erkennung der Schnittstelle (NVMe PCIe-Gen, SATA, USB 2.0/3.x) |
| **Controller** | echte Polling-Rate eines XInput-Controllers (dwPacketNumber), Jitter, Stick-Drift, Live-Anzeige |
| **Internet** | Download, Upload und Bufferbloat (Ping in Ruhe und unter Last, Note A+ bis F) über M-Lab NDT7 – **nur auf eigenen Klick** |
| **Übersicht** | Fazit in einfachem Deutsch, Ampel und Einordnung je Bereich mit Grundlage, Tipps; Rohwerte einklappbar; Export als JSON und HTML |
| **Verlauf** | jeder Lauf wird gespeichert; zwei Läufe mit Differenz in % vergleichen (nur bei gleicher Auflösung) |

Alle Schwellenwerte und Referenzwerte der Bewertung stehen in **`bewertung.json`** neben der Exe und können geprüft und geändert werden.

### Systemvoraussetzungen

- Windows 10 oder Windows 11, **64 Bit** (x64)
- Für den GPU-/Frametime-Test: Grafikkarte mit Direct3D 11
- Für den Controller-Test: XInput-Controller
- Für den Internettest: Internetverbindung (siehe Datenschutz)

### Download und Start

1. Im Bereich **Releases** die ZIP-Datei laden und entpacken.
2. `LAGZERO_Benchmark.exe` starten – keine Installation nötig.

**Windows-Warnung:** Da die Exe nicht mit einem kostenpflichtigen Code-Signing-Zertifikat signiert ist, kann Windows SmartScreen beim ersten Start „Der Computer wurde durch Windows geschützt“ anzeigen. Dann auf **„Weitere Informationen“ → „Trotzdem ausführen“** klicken – aber nur, wenn die Prüfsumme stimmt.

### Prüfsumme (SHA-256)

Die Prüfsumme steht in `SHA256SUMS.txt` im Release. Prüfen in PowerShell:

```powershell
Get-FileHash .\LAGZERO_Benchmark.exe -Algorithm SHA256
```

Der angezeigte Wert muss exakt mit dem Wert in `SHA256SUMS.txt` übereinstimmen.

### Datenschutz

- LAGZERO sendet **nichts ins Internet** – mit einer Ausnahme: die Testverbindungen des **Internettests**, den du selbst startest.
- Der Internettest nutzt **Measurement Lab (M-Lab) NDT7**. M-Lab speichert jede Messung dauerhaft und **veröffentlicht sie öffentlich, einschließlich deiner IP-Adresse, Datum und Uhrzeit** ([M-Lab Datenschutz](https://www.measurementlab.net/privacy/)). Vor jedem Internettest fragt das Programm deshalb um Zustimmung und zeigt die geschätzte Datenmenge.
- Berichte (HTML/JSON) enthalten keinen Benutzernamen, keinen Computernamen und keine Seriennummern. Die USB-Geräteliste wird nur aufgenommen, wenn du „Geräteliste im Bericht“ anhakst.
- Ergebnisse, Verlauf und Logs bleiben lokal im Programmordner (`Verlauf\`, `Berichte\`, `Logs\`).

### Warum M-Lab NDT7 für den Internettest?

- **Cloudflare Speed Test** (speed.cloudflare.com): Es gibt keine veröffentlichten Bedingungen, die die Nutzung der Mess-Endpunkte in eigenen Programmen ausdrücklich erlauben; entsprechende Anfragen im Cloudflare-Forum sind unbeantwortet, und es wird von Sperren (HTTP 429) für Nicht-Browser-Anfragen berichtet ([Forum 1](https://community.cloudflare.com/t/permission-to-use-cloudflare-speed-test-in-a-free-windows-application/973233), [Forum 2](https://community.cloudflare.com/t/public-third-party-use-and-rate-limits-for-cloudflare-speedtest-endpoints/963843)). → nicht verwendet.
- **M-Lab NDT7** ist ein offenes Messprotokoll mit dokumentierter Locate-API und eigenen Referenz-Clients für Drittanbieter; Drittanbieter-Clients müssen die M-Lab-Datenschutzerklärung und Acceptable Use Policy einhalten ([Protokoll](https://github.com/m-lab/ndt-server/blob/main/spec/ndt7-protocol.md), [Locate-API](https://github.com/m-lab/locate/blob/main/USAGE.md), [Datenschutz](https://www.measurementlab.net/privacy/)). → verwendet.
- NDT7 misst laut Spezifikation **mit einer TCP-Verbindung je Richtung**. Auf sehr schnellen Leitungen kann der Wert daher etwas unter dem Maximum liegen.

### Haftungsausschluss

Die Nutzung erfolgt **auf eigene Gefahr**. Der Laufwerkstest schreibt eine Testdatei auf das gewählte Laufwerk (nur in den Ordner `LAGZERO_Benchmark_Temp`, nach ausdrücklicher Bestätigung; die Datei wird danach gelöscht). Belastungstests erzeugen hohe CPU-, GPU- und Speicherlast. Es wird keine Gewähr für Richtigkeit der Ergebnisse oder für Schäden an Hardware oder Daten übernommen. Siehe auch [LICENSE](LICENSE).

### Selbst bauen

```powershell
dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o publish
```

Benötigt das .NET 10 SDK.

---

## English

LAGZERO Benchmark is a Windows program (a single `.exe`, no installer) that measures your PC and explains the results in plain language (German UI) – measured values only, no online comparison database.

### Tests

| Tab | What is measured |
|---|---|
| **System** | CPU, GPU, RAM (size, speed, modules), mainboard, drives, primary monitor (resolution, Hz), Windows version (WMI/registry) |
| **CPU** | integer, floating point and SIMD (AVX2/FMA, automatic fallback without FMA) – single and multi core, score and duration |
| **RAM** | read, write, copy (MB/s) and access latency (ns); hints about free/reserved RAM, slots and memory speed |
| **GPU** | Direct3D 11 render test (FPS, frame time) at a selectable resolution and a compute test (FP32 TFLOPS) |
| **Frame time** | own test window, 10 s: average FPS, 1 % / 0.1 % low, stutters, chart, optional VSync |
| **Drives & USB** | sequential 1 MiB and 4K random (QD1/QD32) with unbuffered I/O; interface detection (NVMe PCIe gen, SATA, USB 2.0/3.x) |
| **Controller** | real polling rate of an XInput controller (dwPacketNumber), jitter, stick drift, live view |
| **Internet** | download, upload and bufferbloat (idle and loaded ping, grade A+ to F) via M-Lab NDT7 – **only on explicit click** |
| **Overview** | plain-language summary, traffic light and rating per area with its basis, tips; raw values collapsible; JSON and HTML export |
| **History** | every run is saved; compare two runs with % difference (only at the same resolution) |

All thresholds and reference values used for the ratings are in **`bewertung.json`** next to the exe and can be reviewed and changed.

### Requirements

- Windows 10 or Windows 11, **64-bit** (x64)
- GPU/frame time test: Direct3D 11 capable graphics card
- Controller test: XInput controller
- Internet test: internet connection (see privacy)

### Download and start

1. Download the ZIP file from **Releases** and extract it.
2. Run `LAGZERO_Benchmark.exe` – no installation required.

**Windows warning:** the exe is not signed with a paid code-signing certificate, so Windows SmartScreen may show "Windows protected your PC" on first start. Click **"More info" → "Run anyway"** – but only if the checksum matches.

### Checksum (SHA-256)

The checksum is in `SHA256SUMS.txt` in the release. Verify in PowerShell:

```powershell
Get-FileHash .\LAGZERO_Benchmark.exe -Algorithm SHA256
```

The value shown must match the value in `SHA256SUMS.txt` exactly.

### Privacy

- LAGZERO sends **nothing to the internet** – with one exception: the test connections of the **internet test**, which you start yourself.
- The internet test uses **Measurement Lab (M-Lab) NDT7**. M-Lab stores every measurement permanently and **publishes it, including your IP address, date and time** ([M-Lab privacy policy](https://www.measurementlab.net/privacy/)). The program therefore asks for consent and shows the estimated data volume before every internet test.
- Reports (HTML/JSON) contain no user name, no computer name and no serial numbers. The USB device list is only included if you tick "Geräteliste im Bericht".
- Results, history and logs stay local in the program folder (`Verlauf\`, `Berichte\`, `Logs\`).

### Why M-Lab NDT7 for the internet test?

- **Cloudflare Speed Test** (speed.cloudflare.com): there are no published terms that explicitly allow using its measurement endpoints in own programs; such questions in the Cloudflare community are unanswered and HTTP 429 blocks for non-browser requests are reported ([forum 1](https://community.cloudflare.com/t/permission-to-use-cloudflare-speed-test-in-a-free-windows-application/973233), [forum 2](https://community.cloudflare.com/t/public-third-party-use-and-rate-limits-for-cloudflare-speedtest-endpoints/963843)). → not used.
- **M-Lab NDT7** is an open measurement protocol with a documented Locate API and reference clients for third parties; third-party clients must comply with M-Lab's privacy policy and acceptable use policy ([protocol](https://github.com/m-lab/ndt-server/blob/main/spec/ndt7-protocol.md), [Locate API](https://github.com/m-lab/locate/blob/main/USAGE.md), [privacy](https://www.measurementlab.net/privacy/)). → used.
- Per specification NDT7 measures **with one TCP connection per direction**, so on very fast lines the result may be slightly below the maximum.

### Disclaimer

**Use at your own risk.** The drive test writes a test file to the selected drive (only into the folder `LAGZERO_Benchmark_Temp`, after explicit confirmation; the file is deleted afterwards). Stress tests cause high CPU, GPU and memory load. No warranty is given for the correctness of results or for damage to hardware or data. See also [LICENSE](LICENSE).

### Build

```powershell
dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o publish
```

Requires the .NET 10 SDK.

---

MIT License · © 2026 Adem Tasdemir · Third-party notices: [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)
