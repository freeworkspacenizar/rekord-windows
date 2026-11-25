# Rekord (Windows)

Aplikasi native Windows 10 (1903+) & Windows 11 (**C# / .NET 8 + WPF + Win32 + DXGI Desktop Duplication**) untuk merekam area layar dengan ukuran dinamis dan mengekspornya ke **Animated WebP** (default, *5–10× smaller size*), **GIF 2-pass**, atau **MP4**.

Folder ini **100% mandiri** — tidak bergantung pada folder lain di luar `Rekord Windows/`.

## Fitur Utama (Feature Parity dengan Rekord macOS + Windows Extras)

- **System Tray App** + popup kontrol cepat & notifikasi balon Windows.
- **Global Hotkey `Ctrl+Shift+6`** via Win32 `RegisterHotKey` (tanpa perlu hak Administrator).
- **Dynamic Selection Overlay** (Per-Monitor DPI V2): 8 resize handle, Aspect Ratio preset (`Free`, `16:9`, `4:3`, `1:1`), **Snap ke Jendela** (`EnumWindows` + `DwmGetWindowAttribute`), dan **Live Pan** saat merekam.
- **Tanpa Border Kuning Windows 10:** Perekaman memakai **DXGI Desktop Duplication** (GPU-accelerated) dengan fallback otomatis ke **GDI `BitBlt`**, serta mengecualikan jendela overlay lewat `SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)`.
- **Countdown HUD (`Off` / `3s`)** & **Highlight Efek Klik Mouse** (klik kiri merah, klik kanan biru via `WH_MOUSE_LL` yang sengaja ikut terekam).
- **Pause / Resume Tanpa Gap:** PTS retiming via `RecordingTimeline` sehingga jeda tidak menyisakan frame beku.
- **Studio Preview & Export:** Video player looping + **Trim Timeline Scrubber**, export **Animated WebP** (`libwebp_anim`), **GIF 2-pass** (`palettegen`/`paletteuse`), dan **MP4** (stream copy atau `h264_mf` MediaFoundation + fallback LGPL).
- **Quick Export & Auto-Copy Clipboard (`CF_HDROP`):** Langsung bisa di-paste sebagai file animasi ke Discord, Slack, Telegram, WhatsApp, Chrome, Notion, dan File Explorer.
- **Start with Windows:** Opsi startup per-user (`HKCU\...\Run`) langsung dari jendela Settings.

## Struktur Folder

- `app/` — Solution .NET 8 (`Rekord.sln`):
  - `src/RekordCore/` — Library murni lintas-platform (`CaptureGeometry`, `RecordingTimeline`, `AppSettings`, `ExportEngine`).
  - `src/Rekord/` — Aplikasi WPF + Win32 (`App/`, `Capture/`, `Native/`, `Overlay/`, `System/`, `UI/`).
  - `tests/RekordCore.Tests/` — 13 unit test xUnit untuk `RekordCore`.
- `vendor/ffmpeg/` — Tempat binary `ffmpeg.exe` LGPL (`win64-lgpl`) + dokumen lisensi (`COPYING.LGPLv2.1`, `LICENSE.md`), diisi otomatis oleh `scripts/fetch-ffmpeg.ps1`.
- `scripts/` — Skrip otomatisasi PowerShell & Inno Setup:
  - `fetch-ffmpeg.ps1` — Mengunduh binary resmi **BtbN FFmpeg `win64-lgpl`** + lisensi LGPL ke `vendor/ffmpeg/`.
  - `generate-icon.ps1` — Generator `Rekord.ico` multi-resolusi (16–256 px).
  - `build-app.ps1` — Menjalankan `dotnet test`, publish self-contained `win-x64`, bundle `ffmpeg.exe` + `ffmpeg-licenses/`, dan membuat `dist/Rekord-win-x64-portable.zip`.
  - `build-installer.ps1` + `Rekord.iss` — Mengemas installer `dist/Rekord-Setup.exe` menggunakan Inno Setup 6.
- `.github/workflows/build-windows.yml` — Workflow GitHub Actions CI (`windows-latest`) untuk test & build otomatis portable `.zip` dan installer `.exe`.

## Cara Build di Windows

Pastikan **.NET 8 SDK** (dan **Inno Setup 6** untuk installer) sudah terpasang di Windows, lalu jalankan di PowerShell dari root folder ini:

```powershell
# 1. Jalankan unit test + build portable self-contained (otomatis unduh ffmpeg win64-lgpl)
.\scripts\build-app.ps1

# 2. Buat Windows Installer (dist\Rekord-Setup.exe)
.\scripts\build-installer.ps1
```

## Cara Menjalankan

- **Installer:** Jalankan `dist\Rekord-Setup.exe`, ikuti wizard instalasi (tidak butuh Administrator), lalu buka **Rekord** dari Start Menu.
- **Portable:** Ekstrak `dist\Rekord-win-x64-portable.zip` lalu jalankan `Rekord.exe`.
- Klik ikon **Rekord** di **System Tray** (pojok kanan bawah) atau tekan **`Ctrl+Shift+6`**.
