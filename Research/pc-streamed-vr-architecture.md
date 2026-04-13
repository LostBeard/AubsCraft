# PC-Streamed VR Architecture

**Date:** 2026-04-13
**Status:** Research + Implementation Plan
**Priority:** High - enables full-quality VR without Quest GPU constraints

---

## Concept

The PC runs the full AubsCraft WebGPU renderer at maximum quality (4070 GPU, no vertex budget limits). The rendered frame is captured, encoded, and streamed over WebRTC to the Quest 3S headset. The Quest acts as a thin client - displaying the video stream in VR and sending head tracking + controller input back to the PC.

This gives Quest users desktop-quality rendering with zero GPU compute on the headset.

---

## Three Viewer Modes

| Mode | Renders On | Displays On | Quality | Requirement |
|---|---|---|---|---|
| **Desktop** | PC | PC browser | Full quality | Browser with WebGPU |
| **Standalone VR** | Quest | Quest | Budget LOD, compact verts | Quest + WiFi |
| **PC-Streamed VR** | PC | Quest (thin client) | Full quality, no limits | PC + Quest on same network |

Standalone is essential for portability (no PC required). PC-Streamed is the premium at-home experience.

---

## Network Requirements

| Spec | Minimum | TJ's Setup |
|---|---|---|
| Bandwidth | ~50 Mbps | 1 Gbps LAN (PC) + 6GHz WiFi 6E (Quest) |
| Latency | <10ms round-trip | <2ms LAN, <5ms 6GHz WiFi |
| Protocol | WebRTC (UDP, built-in) | Same |

6GHz WiFi 6E provides ~500+ Mbps practical throughput with low latency - well above the ~50-100 Mbps needed for stereo 90Hz video.

---

## Streaming Pipeline

### PC Side (Sender)

```
[WebGPU Render] -> [Canvas] -> [MediaStream Capture] -> [H.264/H.265 Encode (NVENC)]
       -> [WebRTC RTCPeerConnection] -> [Network to Quest]

[WebRTC DataChannel] <- [Receive head pose + controller data from Quest]
       -> [Apply to camera for next frame]
```

### Quest Side (Receiver)

```
[WebXR Session] -> [Head pose + controller data]
       -> [WebRTC DataChannel] -> [Send to PC]

[WebRTC RTCPeerConnection] <- [Receive video stream from PC]
       -> [Hardware H.264 Decode] -> [VideoFrame]
       -> [Draw to WebXR framebuffer as textured quad]
```

---

## Implementation Steps

### Step 1: Canvas Capture on PC

The WebGPU canvas can be captured as a MediaStream:

```csharp
// SpawnDev.BlazorJS already has MediaStream wrappers
using var stream = _canvas.CaptureStream(90); // 90 FPS capture
// or use MediaStreamTrack from canvas
```

Alternative using WebCodecs for more control:
```csharp
// Capture individual frames as VideoFrame
using var frame = new VideoFrame(_canvas);
// Encode via VideoEncoder (hardware NVENC when available)
```

### Step 2: WebRTC Peer Connection

Use SpawnDev.BlazorJS.SimplePeer or raw RTCPeerConnection:

```csharp
// PC side - add video track
var pc = new RTCPeerConnection(iceConfig);
var videoTrack = stream.GetVideoTracks()[0];
pc.AddTrack(videoTrack, stream);

// Data channel for tracking data (low latency, unordered)
var dataChannel = pc.CreateDataChannel("tracking", new RTCDataChannelInit
{
    Ordered = false,       // unordered for lowest latency
    MaxRetransmits = 0     // no retransmits - stale data is useless
});
```

### Step 3: Quest Thin Client

The Quest runs a minimal page that:
1. Starts WebXR `immersive-vr` session
2. Connects to PC via WebRTC (same tracker infrastructure as SpawnDev.WebTorrent)
3. Receives video stream and renders it as a texture
4. Sends head pose + controller data via data channel

```csharp
// Quest side - receive video
pc.OnTrack += (sender, e) =>
{
    _remoteStream = e.Streams[0];
    _videoElement.SrcObject = _remoteStream;
};

// Send head pose every frame via data channel
void OnXRFrame(XRFrame frame)
{
    var pose = frame.GetViewerPose(refSpace);
    var poseData = SerializePose(pose); // position + orientation
    var controllerData = SerializeControllers(frame);
    dataChannel.Send(poseData + controllerData);

    // Draw received video to XR framebuffer
    RenderVideoToVR(frame);
}
```

### Step 4: Video-to-VR Display

On Quest, the received video frame is drawn as a texture on two quads (one per eye):

```csharp
// Option A: HTMLVideoElement as WebGPU texture source
var texture = device.ImportExternalTexture(new GPUExternalTextureDescriptor
{
    Source = _videoElement
});
// Render fullscreen quad per eye with the video texture

// Option B: VideoFrame + copyExternalImageToTexture
device.Queue.CopyExternalImageToTexture(
    new GPUCopyExternalImageSourceInfo { Source = videoFrame },
    new GPUCopyExternalImageDestInfo { Texture = vrTexture },
    new long[] { width, height }
);
```

### Step 5: Pose Application on PC

PC receives head pose from Quest and applies it to the camera:

```csharp
dataChannel.OnMessage += (sender, e) =>
{
    var (position, orientation, controllers) = DeserializeTracking(e.Data);
    _camera.Position = position;
    _camera.Rotation = orientation;
    _controllerState = controllers;
};
```

### Step 6: Stereo Rendering on PC

PC must render two views (left eye + right eye) and encode them as a single side-by-side frame:

```
[Left Eye | Right Eye]  -> encode as single H.264 frame
```

Quest splits the received frame and displays each half to the corresponding eye.

Resolution: Quest 3S is 1832x1920 per eye = 3664x1920 side-by-side.
At 90fps H.264 High Profile: ~50-80 Mbps bitrate for good quality.

---

## Latency Analysis

| Stage | Time | Cumulative |
|---|---|---|
| Quest sends head pose (data channel) | ~1ms | 1ms |
| Network to PC | ~2ms | 3ms |
| PC processes pose + renders frame | ~11ms (90fps) | 14ms |
| H.264 encode (NVENC hardware) | ~3ms | 17ms |
| Network to Quest | ~2ms | 19ms |
| H.264 decode (Quest hardware) | ~3ms | 22ms |
| Display scanout | ~5ms | 27ms |
| **Total motion-to-photon** | | **~27ms** |

Native Quest VR: ~15ms motion-to-photon. Streamed adds ~12ms.

### Latency Mitigations

**1. Asynchronous TimeWarp (ATW)**
Quest applies ATW natively - it reprojects the last rendered frame based on the latest head pose at display time. This covers small head rotations between frames. The streamed video benefits from this automatically if we render at the correct projection.

**2. Wider FOV Rendering**
PC renders 10-15% wider than Quest's FOV. Quest crops/shifts based on latest head position. Small head movements are covered without waiting for a new frame. Cost: ~20% more pixels to render (trivial for a 4070).

**3. Predictive Pose**
Send not just current pose but also velocity/acceleration. PC predicts where the head will be when the frame arrives (~27ms in future). Render at the predicted pose. Reduces perceived latency significantly.

```csharp
// Quest sends pose + velocity
var posePacket = new PosePacket
{
    Position = pose.Position,
    Orientation = pose.Orientation,
    LinearVelocity = ComputeVelocity(currentPose, lastPose, dt),
    AngularVelocity = ComputeAngularVelocity(currentOrientation, lastOrientation, dt),
    Timestamp = performance.now()
};

// PC predicts future pose
float predictionMs = 27f; // estimated pipeline latency
var predictedPose = ExtrapolatePose(posePacket, predictionMs);
camera.ApplyPose(predictedPose);
```

**4. Adaptive Bitrate**
Monitor WebRTC stats (RTCStatsReport). If packet loss or jitter increases, reduce encoding quality/resolution. If network is clean, increase quality.

**5. Sliced Encoding**
Encode the frame in horizontal slices. Send top slices first. Quest can start decoding and displaying the top of the frame before the bottom arrives. Reduces effective latency by half a frame.

---

## Connection Flow

### Discovery

Use the existing SpawnDev.WebTorrent tracker infrastructure:

1. PC starts the viewer, generates a session ID (or QR code)
2. PC announces on `wss://hub.spawndev.com/announce` with session info hash
3. Quest opens a "Connect to PC" page, enters session ID (or scans QR code)
4. Quest announces on same tracker
5. WebRTC signaling via tracker (same as SpawnDev.WebTorrent peer connections)
6. Direct WebRTC connection established over LAN

### Alternative: Direct LAN Discovery

1. PC starts a WebSocket server on a known port (e.g., 18770 - same as TorrentHttpServer)
2. Quest connects directly via `ws://PC_IP:18770/vr-stream`
3. Establish WebRTC from there
4. No external tracker needed for LAN

---

## Encoding Options

### Option A: Canvas.captureStream() + RTCPeerConnection (Simplest)

```csharp
using var stream = canvas.CaptureStream(90);
pc.AddTrack(stream.GetVideoTracks()[0], stream);
```

Browser handles encoding automatically. Least control but simplest to implement. Chrome uses hardware encoding when available.

### Option B: WebCodecs VideoEncoder (Most Control)

```csharp
var encoder = new VideoEncoder(new VideoEncoderInit
{
    Output = (chunk, metadata) => { /* send via data channel or custom transport */ },
    Error = (e) => { Console.Error.WriteLine(e); }
});
await encoder.Configure(new VideoEncoderConfig
{
    Codec = "avc1.640033",      // H.264 High Profile Level 5.1
    Width = 3664,                // side-by-side stereo
    Height = 1920,
    Framerate = 90,
    Bitrate = 60_000_000,       // 60 Mbps
    LatencyMode = "realtime",   // optimize for latency, not quality
    HardwareAcceleration = "prefer-hardware"  // use NVENC
});

// Per frame:
using var frame = new VideoFrame(canvas, new VideoFrameInit { Timestamp = microseconds });
encoder.Encode(frame, new VideoEncoderEncodeOptions { KeyFrame = isKeyFrame });
```

More control over bitrate, latency mode, hardware acceleration. But requires manual transport.

### Option C: MediaRecorder + WebSocket (Simplest Fallback)

```csharp
var recorder = new MediaRecorder(stream, new MediaRecorderOptions
{
    MimeType = "video/webm;codecs=vp8",
    VideoBitsPerSecond = 60_000_000
});
recorder.OnDataAvailable += (e) => webSocket.Send(e.Data);
recorder.Start(11); // 11ms timeslice for ~90fps chunks
```

Higher latency (MediaRecorder buffers) but simplest fallback. Not recommended for VR.

### Recommendation: Option A for MVP, Option B for production.

Canvas.captureStream() gets something working fast. WebCodecs gives the control needed for latency tuning.

---

## SpawnDev.BlazorJS Wrappers Available

All of these exist or can be added:
- `RTCPeerConnection` - full WebRTC peer connection
- `MediaStream` / `MediaStreamTrack` - media capture
- `HTMLVideoElement` - video playback (receiver side)
- `VideoFrame` - WebCodecs frame (may need wrapper)
- `VideoEncoder` / `VideoDecoder` - WebCodecs (may need wrapper)
- `XRSession` / `XRFrame` / `XRViewerPose` - WebXR (already wrapped)
- `RTCDataChannel` - low-latency data transport
- `GamepadHapticActuator` - controller vibration feedback

## Quest Browser Capabilities

- WebGPU: Supported (experimental, behind flag as of early 2026)
- WebXR: Fully supported (`immersive-vr` and `immersive-ar`)
- WebRTC: Fully supported (H.264 hardware decode)
- WebCodecs: Supported (hardware H.264 decode)
- SharedArrayBuffer: Supported with COOP/COEP headers

---

## AR Tabletop Mode Implications

The PC-Streamed architecture also enables the AR tabletop editor vision:

- PC renders the diorama view (bird's eye, miniature scale)
- Stream to Quest in `immersive-ar` mode (passthrough + rendered overlay)
- Quest shows the miniature world on the real table
- TJ edits from above while Aubs plays in standalone VR mode
- Both connected to the same server, changes sync in real-time

The streamed diorama needs LOWER resolution than full VR (the world is small on the table) so bandwidth requirements are lighter.

---

## Implementation Priority

1. **MVP:** Canvas.captureStream() + RTCPeerConnection + Quest thin client page. Get video flowing.
2. **Tracking:** Add data channel for head pose + controller. Apply to PC camera.
3. **Stereo:** Side-by-side rendering on PC, split display on Quest.
4. **Latency:** Predictive pose, wider FOV render, adaptive bitrate.
5. **Polish:** QR code pairing, auto-discovery on LAN, quality settings.
6. **AR:** Extend thin client for immersive-ar diorama mode.
