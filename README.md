<p align="center">
  <img src="docs/logo.png" width="96" alt="LiteRec logo">
</p>

<h1 align="center">LiteRec</h1>

<p align="center">
  <b>Grabador de pantalla ultraligero para Windows y Linux</b><br>
  60 fps · captura y codificación por GPU · casi sin CPU ni RAM
</p>

<p align="center">
  <img alt="Windows" src="https://img.shields.io/badge/Windows-10%20%7C%2011-0078D6">
  <img alt="Linux" src="https://img.shields.io/badge/Linux-X11%20%7C%20Wayland-FCC624?logo=linux&logoColor=black">
  <img alt=".NET" src="https://img.shields.io/badge/.NET-10-512BD4">
  <img alt="License" src="https://img.shields.io/badge/license-MIT-green">
  <a href="../../actions/workflows/build.yml"><img alt="Build" src="../../actions/workflows/build.yml/badge.svg"></a>
</p>

<p align="center">
  <img src="docs/screenshot.png" width="760" alt="LiteRec">
</p>

---

## ✨ Características

- 🎥 **60 fps constantes** a 720p, 900p, 1080p o resolución nativa
- ⚡ **Captura y codificación por GPU** en Windows y en Linux
- 🧠 H.264 / H.265 con **Intel Quick Sync, NVIDIA NVENC, AMD AMF o VAAPI**
- 🔊 Audio del sistema y/o micrófono
- 🖱️ Cursor opcional (y resaltado de clics en Windows)
- ⌨️ Atajos: **F9** grabar/detener · **F10** pausar/reanudar
- 📊 Estadísticas en vivo: fps, tamaño del archivo, CPU y RAM
- 🎨 Interfaz oscura hecha con [Avalonia](https://avaloniaui.net), igual en los dos sistemas
- 💾 Recuerda tus ajustes entre sesiones

## 🖥️ Cómo graba en cada sistema

| | Windows | Linux |
|---|---|---|
| **Captura** | Desktop Duplication (pantalla) / Windows.Graphics.Capture (ventana) | [gpu-screen-recorder](https://git.dec05eba.com/gpu-screen-recorder/about/) (X11 y Wayland) o FFmpeg `x11grab` (solo X11) |
| **Codificación** | Media Foundation por hardware | VAAPI / NVENC (o x264 si no hay GPU compatible) |
| **Audio** | WASAPI | PulseAudio / PipeWire |
| **Atajos F9/F10** | Globales (funcionan minimizada) | Con la ventana enfocada |
| **Ocultar la app del video** | ✅ | — |

En Linux, LiteRec busca primero **gpu-screen-recorder**, que hace todo dentro de la GPU como la versión de Windows. Si no lo encuentra, usa **FFmpeg** y prueba NVENC, luego VAAPI y por último x264.

## 🚀 Rendimiento

En una laptop con Intel HD 520 (Windows), grabando 1280×720 a 60 fps:

| CPU media | RAM | Video |
|-----------|-----|-------|
| ~8 %      | ~140 MB | 60.00 fps, ~6 Mbps |

La imagen se captura, se escala y se codifica dentro de la GPU, así que los fotogramas nunca pasan por la RAM. No hay vista previa en vivo, la interfaz se refresca una vez por segundo y la memoria se libera al terminar cada grabación.

## 📥 Descargar

Ve a **[Releases](../../releases/latest)**.

### Windows
1. Descarga `LiteRec-win-x64.zip`, descomprímelo y abre `LiteRec.exe`.
2. Requisitos: Windows 10 (2004+) u 11 de 64 bits, [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) y [Visual C++ Redistributable x64](https://aka.ms/vs/17/release/vc_redist.x64.exe).

### Linux
```bash
tar -xzf LiteRec-linux-x64.tar.gz
cd LiteRec
./install.sh        # lo añade al menú de aplicaciones (opcional)
./LiteRec           # o ábrelo directamente
```
No necesita .NET instalado. Para grabar instala uno de estos:
- **Recomendado (X11 y Wayland):** `flatpak install flathub com.dec05eba.gpu_screen_recorder`
- **Solo X11:** FFmpeg (`sudo apt install ffmpeg`, `sudo dnf install ffmpeg`, `sudo pacman -S ffmpeg`…)

Los videos se guardan en tu carpeta de Videos, dentro de `LiteRec`. Puedes cambiarla desde la app.

## 🛠️ Compilar desde el código

```bash
git clone https://github.com/luipytonudo/LiteRec.git
cd LiteRec

# Windows
dotnet publish src/LiteRec -c Release -f net10.0-windows -o App

# Linux
dotnet publish src/LiteRec -c Release -f net10.0 -r linux-x64 --self-contained -o App
```

Cada push compila las dos versiones en GitHub Actions. En Linux, además, hace una grabación de prueba real con Xvfb + FFmpeg. Al publicar una release, los paquetes se adjuntan solos.

### Estructura

```
LiteRec/
├─ src/LiteRec/
│  ├─ Core/                     # código común
│  │  ├─ RecordingEngine.cs     # interfaz del motor + tipos
│  │  ├─ AppSettings.cs         # ajustes (%AppData% o ~/.config)
│  │  └─ NativeMethods.cs       # llamadas nativas (Win32 / libc)
│  ├─ Platforms/
│  │  ├─ Windows/               # ScreenRecorderLib (DXGI + Media Foundation)
│  │  └─ Linux/                 # gpu-screen-recorder / FFmpeg
│  ├─ App.axaml                 # tema oscuro y estilos
│  └─ MainWindow.axaml          # interfaz
├─ packaging/linux/             # instalador y acceso directo .desktop
├─ .github/workflows/build.yml  # CI y releases
└─ LiteRec.sln
```

### Modo de prueba

```bash
LiteRec --selftest 6 prueba.mp4
```

Graba 6 segundos de la primera pantalla y muestra el backend usado, los fps, la CPU y la RAM. Devuelve 0 si salió bien.

## 🤝 Contribuir

Los *issues* y *pull requests* son bienvenidos. Algunas ideas:
- Atajos globales en Linux (X11 / portal GlobalShortcuts)
- Webcam superpuesta
- Grabar solo una región de la pantalla
- Paquete Flatpak / AppImage
- Versión para macOS (ScreenCaptureKit)

## 📄 Licencia

[MIT](LICENSE). En Windows usa [ScreenRecorderLib](https://github.com/sskodje/ScreenRecorderLib) (MIT). En Linux llama como programas externos a [gpu-screen-recorder](https://git.dec05eba.com/gpu-screen-recorder/about/) (GPL-3.0) o a [FFmpeg](https://ffmpeg.org) (LGPL/GPL), que no se incluyen. Más detalles en [THIRD-PARTY-NOTICES](THIRD-PARTY-NOTICES.md).
