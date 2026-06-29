# HealLink HoloLens 2

This repository contains the HoloLens 2 Unity endpoint for HealLink. It receives remote Quest hand and drawing data, renders it into a mixed-reality scene, exposes the local device IP in view, and uses LAN WebRTC signaling for bidirectional audio.

## Project Layout

| Path | Purpose |
| --- | --- |
| `Assets/Scenes/HealLinkHoloLens2.unity` | Main HoloLens scene. Contains the camera, MRTK playspace, remote hand rigs, IP overlay, and the Quest receiver object. |
| `Assets/Scripts/` | Project-owned runtime scripts. These contain the networking, hand retargeting, drawing, audio, and overlay logic. |
| `Assets/Editor/` | Project-owned editor/build helpers for UWP manifest preparation and WebRTC-compatible HoloLens build settings. |
| `Assets/MRTK/` and `Assets/MixedRealityToolkit.Generated/` | MRTK shader assets and generated MRTK profile/configuration data. |
| `Assets/XR/` | OpenXR and XR loader settings used by the HoloLens build. |
| `Packages/` | Unity package manifest plus local Mixed Reality package archives and the MixedReality-WebRTC package. |
| `ProjectSettings/` | Unity project, player, XR, quality, input, and build settings. |

## HoloLens-Side Architecture

The HoloLens scene is built around one main runtime receiver:

1. `QuestHandDataReceiver` listens for Quest-side UDP packets on port `5055`.
2. Incoming UDP bytes are decoded into JSON packets and queued from a background receive thread.
3. Unity's main thread processes queued packets, because scene objects and transforms must be touched from the main thread.
4. Hand packets are converted from Quest camera-local poses into HoloLens world poses when `poseSpace` is `CameraLocal`.
5. Normalized joint names are mapped onto the existing MRTK hand rig transforms.
6. Stroke events create and update `LineRenderer` objects for remote drawing.
7. Audio normally runs through `WebRtcAudioPeer`, which builds a runtime MixedReality-WebRTC audio peer and uses `LanWebRtcSignaler` for local UDP signaling.
8. `HololensIpOverlay` anchors a small IP label in front of the camera so the Quest side can connect to the correct HoloLens address.

The runtime data flow is:

```text
Quest app
  -> UDP hand/stroke packets
  -> QuestHandDataReceiver
  -> pose conversion
  -> MRTK hand rigs + remote drawing lines

Quest app
  <-> LAN WebRTC signaling
  <-> WebRtcAudioPeer
  <-> HoloLens microphone/audio playback
```

The project no longer writes runtime debug captures such as WAV, CSV, or screenshot files. Build-time icon generation still writes the required UWP logo PNG files into Unity's generated build folder.

## Runtime Scripts

| Script | Role |
| --- | --- |
| `QuestHandDataReceiver.cs` | Core HoloLens receiver. Handles UDP packet intake, Quest-to-HoloLens pose conversion, MRTK hand retargeting, remote drawing, and MRTK pointer visibility settings. |
| `WebRtcAudioPeer.cs` | Creates the runtime MixedReality-WebRTC peer connection, microphone source, audio receiver, output `AudioSource`, and LAN signaler. |
| `LanWebRtcSignaler.cs` | Exchanges SDP and ICE messages over UDP. Media does not travel through this socket; it only bootstraps the WebRTC peer-to-peer audio connection. |
| `HololensIpOverlay.cs` | Shows the current local IPv4 address in a camera-anchored world overlay, with a GUI fallback for editor/play-mode visibility. |
| `MetaQuestHandDataSender.cs` | Quest-side helper/protocol reference. It reads Meta Quest hand skeleton data and sends packets that match the HoloLens receiver format. It is not part of the HoloLens scene runtime. |

## Editor And Build Scripts

| Script | Role |
| --- | --- |
| `BuildPostProcessor.cs` | Updates the generated UWP manifest with the HealLink package identity, required HoloLens capabilities, and app logo files. |
| `HoloLensWebRtcBuildArchitecture.cs` | Keeps UWP builds on `ARM`, which matches the native plugin architecture provided by MixedReality-WebRTC 2.0.2 for HoloLens. |

## Scene Objects

| Object | Role |
| --- | --- |
| `MixedRealityPlayspace` | MRTK/OpenXR camera rig and parent for the left/right MRTK hand rigs. |
| `Main Camera` | HoloLens camera, gaze/input components, IP overlay controller, and camera-anchored IP overlay children. |
| `QuestHandDataReceiver` | Runtime receiver object for Quest hand/stroke packets and WebRTC audio setup. |
| `MixedRealityToolkit` | MRTK service root using the generated HealLink MRTK profile. |

## Network Defaults

| Channel | Default |
| --- | --- |
| Quest hand/stroke UDP packets | HoloLens listens on UDP `5055`. |
| WebRTC signaling on HoloLens | HoloLens listens on UDP `5076`. |
| WebRTC signaling on Quest | Quest listens on UDP `5077`. |
| IP overlay refresh | Once per second. |

## Unity Build Settings

Use these settings when building from Unity:

- Unity version: `2020.3.42f1`
- Platform: Universal Windows Platform
- Target device: HoloLens
- Architecture: ARM
- Build type: D3D Project
- App/product name: `HealLink Trainee`
- UWP package name: `HealLink-Trainee`
- Build scene: `Assets/Scenes/HealLinkHoloLens2.unity`

The editor helper `HealLink/Prepare HoloLens WebRTC Build` can be used to force the correct UWP architecture before building.

## Git Hygiene

The repository uses a Unity-focused `.gitignore`. Unity-generated folders such as `Library/`, `Temp/`, `Build/`, `Logs/`, and `UserSettings/` are ignored. Unity `.meta` files inside tracked asset folders must stay committed because Unity uses them for stable scene and prefab references.

## License

This repository is released under the MIT License. See [LICENSE](LICENSE) for details.
