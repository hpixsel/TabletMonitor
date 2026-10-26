# TabletMonitor – communication protocol

All multi-byte numbers are **big-endian**. Strings in JSON payloads are UTF-8.

## Ports

| Port  | Transport | Purpose                                        |
|-------|-----------|------------------------------------------------|
| 47800 | TCP       | Session (the tablet listens, the PC connects)  |
| 47801 | UDP       | Discovery (both sides listen)                  |

Over USB, the PC runs `adb forward tcp:<local> tcp:47800` and connects to `127.0.0.1:<local>`; the rest of the protocol is identical.

## Discovery (UDP 47801)

Datagram: `"TMD1"` (4 bytes ASCII) + `u8 type` + payload.

| type | Direction        | Payload                                    |
|------|------------------|--------------------------------------------|
| 1    | PC → broadcast   | JSON `{"id": "...", "name": "DESKTOP-ABC", "version": 1}` |
| 2    | tablet → PC / broadcast | JSON `DeviceInfo` (below)           |
| 3    | tablet → PC      | JSON `DeviceInfo` – "please connect to me" (user tapped the PC on the tablet) |

The tablet answers every probe (type 1) with a unicast type 2 and additionally broadcasts a type 2 every 2 s while it is not connected. Probes double as PC announcements: the tablet lists every PC it hears from, and the user can ask one of them to connect (type 3). The PC asks its user to approve a device the first time.

`DeviceInfo`:
```json
{ "id": "a1b2c3", "name": "Galaxy Tab S9", "model": "SM-X710",
  "width": 2560, "height": 1600, "refresh": 120, "dpi": 340,
  "port": 47800, "version": 1, "busy": false }
```
`width`/`height` are the physical screen dimensions in landscape orientation (width ≥ height).

## TCP session

Frame: `u8 type` + `u32 length` + `payload[length]`.

### PC → tablet

| type | Name          | Payload |
|------|---------------|---------|
| 1    | HELLO         | JSON `{"name": "...", "version": 1}` |
| 2    | VIDEO_CONFIG  | JSON `{"width": 2560, "height": 1600, "codec": "h264"}` – sent before the first frame and after every resolution change |
| 3    | VIDEO_FRAME   | `u8 flags` (bit0 = keyframe) + `i64 ptsMicros` + Annex-B H.264 |
| 4    | CURSOR_SHAPE  | `u16 w` + `u16 h` + `u16 hotX` + `u16 hotY` + `w*h` × `u32 ARGB` (non-premultiplied) |
| 5    | CURSOR_POS    | `i16 x` + `i16 y` + `u8 visible` (pixels in video-frame space) |
| 6    | PING          | `i64 token` |
| 7    | BYE           | – |

### Tablet → PC

| type | Name              | Payload |
|------|-------------------|---------|
| 1    | HELLO             | JSON `DeviceInfo` |
| 7    | BYE               | – |
| 10   | POINTERS          | `u8 count` + `count` × pointer (below) |
| 11   | REQUEST_KEYFRAME  | – |
| 12   | PONG              | `i64 token` |
| 13   | VIEW_SIZE         | `u16 width` + `u16 height` – current size of the video view (rotation) |

Pointer (16 bytes): `u8 id` + `u8 state` + `u8 tool` + `u8 buttons` + `f32 x` + `f32 y` + `f32 pressure`.

* `state`: 0 = down, 1 = move, 2 = up, 3 = cancel, 4 = hover (pen only), 5 = hover exit
* `tool`: 0 = finger, 1 = stylus, 2 = eraser
* `buttons`: bit0 = stylus barrel button
* `x`, `y` ∈ [0, 1] relative to the video frame; `pressure` ∈ [0, 1]

The POINTERS message always carries the **complete** current state (all active pointers), with `state` describing what happened to each one in this event.
