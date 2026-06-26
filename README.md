# HealLink HoloLens 2

This repository contains the HoloLens 2 Unity project for HealLink. It includes the mixed-reality scene, device-side scripts, project settings, and package dependencies needed for the HoloLens 2 endpoint.

## Purpose

The HoloLens 2 project is the mixed-reality endpoint of HealLink. It runs on HoloLens 2 and provides the device-side Unity scene, MRTK/OpenXR setup, HL2SS integration, and networking components needed to exchange data with the other HealLink parts.

## Main Capabilities

- HoloLens 2 Unity application based on Unity `2020.3.42f1`.
- MRTK and OpenXR configuration for HoloLens 2.
- HL2SS initialization for HoloLens sensor and system streams.
- Receiving Quest-side hand tracking packets over UDP.
- Retargeting remote Quest hand poses onto MRTK hand rigs.
- Receiving and rendering remote drawing/stroke events.
- Bidirectional audio support through LAN WebRTC signaling.
- Optional microphone streaming and WAV diagnostics for debugging.
- Local HoloLens IP overlay and helper scripts for device setup.

## Important Project Paths

| Path | Description |
| --- | --- |
| `Assets/Scenes/HealLinkHoloLens2.unity` | Main Unity scene for the HoloLens 2 endpoint. |
| `Assets/Scripts/Hololens2SensorStreaming.cs` | Starts HL2SS interfaces for HoloLens 2 sensor streaming. |
| `Assets/Scripts/QuestHandDataReceiver.cs` | Receives Quest hand, stroke, and audio packets and applies them in the HoloLens scene. |
| `Assets/Scripts/WebRtcAudioPeer.cs` | Creates the runtime WebRTC audio peer. |
| `Assets/Scripts/LanWebRtcSignaler.cs` | Exchanges WebRTC signaling messages over UDP on the local network. |
| `Assets/Scripts/HololensMicrophoneStreamServer.cs` | Streams HoloLens microphone audio over TCP for diagnostics or integration. |
| `Packages/manifest.json` | Unity package dependencies, including local Mixed Reality packages. |
| `ProjectSettings/` | Unity project and XR settings. |

## Git Hygiene

The repository uses a Unity-focused `.gitignore`. The following folders and generated files are intentionally ignored:

- `Library/`, `Temp/`, `Obj/`, `Build/`, `Builds/`, `Logs/`, `UserSettings/`
- IDE folders such as `.vs/` and `.idea/`
- generated `.csproj`, `.sln`, and related editor files
- local audio/debug captures and Unity recovery scenes

Unity `.meta` files inside `Assets/`, `Packages/`, and `ProjectSettings/` are kept because Unity uses them for stable asset references.

## License

This repository is released under the MIT License. See [LICENSE](LICENSE) for details.
