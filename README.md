# OpenWrt Board Finder

A small Windows desktop utility that discovers OpenWrt boards on the local network
via a UDP broadcast and opens their web interface with a double-click.

It is the desktop counterpart of the `locator` daemon running on the board
(the daemon that answers discovery requests on UDP port 23 with the board type,
board ID, MAC address, firmware version and application title).

![platform](https://img.shields.io/badge/platform-Windows-blue)
![framework](https://img.shields.io/badge/.NET-8.0--windows-512BD4)
![ui](https://img.shields.io/badge/UI-WPF-lightgrey)

---

## Features

- Discovers every board that answers the discovery request, on **all** active IPv4 interfaces at once.
- Shows IP address, MAC address, client IP, firmware version and application title.
- **Double-click a row** to open `http://<board-ip>` in the default browser.
- Falls back to a Windows ARP lookup (`SendARP`) when the board's reply does not carry a MAC address.
- Sends to both the limited broadcast address (`255.255.255.255`) and the subnet-directed
  broadcast address, because some adapters silently drop the former.
- Bounded scan time: the scan always terminates, even if unrelated traffic is arriving on UDP port 23.

## Screenshot

```
┌──────────────────────────────────────────────────────────────────────┐
│                     Available OpenWrt Boards                         │
├───────────────┬─────────────────┬──────────────┬─────────┬───────────┤
│ IP Address    │ MAC Address     │ Client IP    │ Version │ Applica…  │
├───────────────┼─────────────────┼──────────────┼─────────┼───────────┤
│ 192.168.1.42  │ 00:0A:35:11:22… │ 192.168.1.10 │ 1.2.3.4 │ FM SDR    │
│ 192.168.1.57  │ 00:0A:35:AA:BB… │ 192.168.1.10 │ 1.2.0.0 │ Zynq Board│
└───────────────┴─────────────────┴──────────────┴─────────┴───────────┘
   Boards found: 2              [  Refresh  ]  [   Exit   ]
```

---

## Requirements

| | |
|---|---|
| OS | Windows 10 / 11 (x64) |
| Runtime | [.NET 8.0 **Desktop** Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) |
| Build | .NET 8 SDK, or Visual Studio 2022 (17.8+) |

> The app is *framework-dependent*. If the .NET 8 Desktop Runtime is missing, Windows
> shows its own "framework not found" dialog and offers the download link — the app
> itself never gets to run, so it deliberately does not try to detect the runtime.
> To ship a build that needs no runtime at all, publish it self-contained (see below).

## Build

```bash
git clone https://github.com/<your-user>/Finder.git
cd Finder
dotnet build -c Release
```

Or open `Finder.sln` in Visual Studio 2022 and press **F5**.

### Publish

Framework-dependent, single file:

```bash
dotnet publish -c Release -r win-x64 --self-contained false ^
  -p:PublishSingleFile=true
```

Self-contained (no .NET installation needed on the target machine):

```bash
dotnet publish -c Release -r win-x64 --self-contained true ^
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

The result lands in `bin/Release/net8.0-windows/win-x64/publish/`.

## Usage

1. Make sure the `locator` daemon is running on the board and that the PC is on the same L2 segment
   (a broadcast does not cross routers).
2. Start `Finder.exe`. A scan starts automatically.
3. Press **Refresh** to scan again.
4. **Double-click** a board to open its LuCI web interface.

### Firewall

The first run usually raises a Windows Defender Firewall prompt. Allow the app on your
**private** network — without it, the replies from the boards are dropped and the list stays empty.

---

## Discovery protocol

All multi-byte fields are raw bytes; there is no endianness conversion.
The `length` field is the size of the **whole** packet, including the trailing checksum byte.
The checksum is chosen so that the sum of all `length` bytes is `0 (mod 256)`.

### Request (PC → broadcast, UDP port 23)

| Offset | Size | Value | Meaning |
|---|---|---|---|
| 0 | 1 | `0xFF` | `TAG_CMD` |
| 1 | 1 | `0x04` | packet length |
| 2 | 1 | `0x02` | `CMD_DISCOVER_TARGET` |
| 3 | 1 | `0xFB` | checksum |

On the wire: `FF 04 02 FB`.

### Reply (board → PC)

| Offset | Size | Meaning | Present when |
|---|---|---|---|
| 0 | 1 | `TAG_STATUS` = `0xFE` | always |
| 1 | 1 | packet length | always |
| 2 | 1 | `CMD_DISCOVER_TARGET` = `0x02` | always |
| 3 | 1 | board type | `length > 5` |
| 4 | 1 | board ID | `length > 5` |
| 5–8 | 4 | client IP (the address the board sees us on) | `length > 9` |
| 9–14 | 6 | MAC address | `length > 15` |
| 15–18 | 4 | firmware version | `length > 19` |
| 19–82 | 64 | application title, NUL-terminated | `length > 83` |
| last | 1 | checksum | always |

A full reply is therefore 84 bytes.

The application title is decoded as **UTF-8** and cut at the **first** NUL byte —
whatever follows the terminator inside the 64-byte field is ignored.

The firmware version is rendered as four dotted decimal bytes (`1.2.3.4`). If your
`locator` build packs that field differently, adjust the single line in `ProcessResponse()`.

### Scan timing

| Phase | Timeout |
|---|---|
| Waiting for the first reply | 5 s |
| Waiting for further replies | 1 s |
| Hard limit for the whole scan | 15 s |
| Maximum number of boards | 256 |

---

## Project layout

```
Finder/
├── Finder.sln
├── Finder.csproj
├── App.xaml / App.xaml.cs          global exception handlers
├── MainWindow.xaml                 window layout, columns, buttons
├── MainWindow.xaml.cs              discovery, parsing, ARP fallback
├── AssemblyInfo.cs
├── image.ico
└── README.md
```

## Troubleshooting

| Symptom | Cause / fix |
|---|---|
| "No boards found." | Firewall blocking UDP/23, board on another subnet, or `locator` not running. On the board: `/etc/init.d/locator status` |
| "Could not open any UDP socket (port 23 may be in use)." | Another service holds UDP port 23 on every interface. The app already retries on an ephemeral port; if that also fails, stop the conflicting service. |
| MAC shown as `N/A` | The reply was short (no MAC field) and the ARP lookup failed — normal for a board that is not yet in the ARP cache or is off-link. |
| Application column empty | The reply is shorter than 84 bytes, so the board is not sending the title. |

## License

MIT — see [LICENSE](LICENSE).
# Finder
