# ROS-ATLAS-Pipeline

Stream live ROS 2 data into **ATLAS** through Open Streaming — with no per-robot code.
The bridge reads each topic's message definition at runtime and turns every numeric field
into an ATLAS parameter automatically. Anything that can run `foxglove_bridge` works.





**You don't need a robot to try it.** Two proven data sources:

| Source | What you need | Guide |
|---|---|---|
| 🖥️ **Gazebo simulation** | Just WSL — no hardware at all | [Quick start: simulation](#quick-start-simulation-no-hardware) |
| 🔌 **CAN input board** | A Raspberry Pi wired to CAN hardware | [Quick start: CAN hardware](#quick-start-can-hardware) |

```
 LINUX SIDE (robot, Pi, or WSL)                       WINDOWS
 ──────────────────────────────                       ───────────────────────────────────
 any ROS 2 topics ─► foxglove_bridge ──WebSocket──► RosAtlasBridge.exe ─► Kafka ─► ATLAS
                     (standard package)   :8765       (this repo)                  Stream Recorder
```

`foxglove_bridge` serves every topic's definition and raw CDR bytes over a WebSocket, so a
plain Windows .NET program can read real ROS data without a ROS install. The bridge flattens
each message into numbers and writes them to Kafka as a live ATLAS session.

> **About the CAN example** — the demo was recorded against a real **SIU-700 / TAG-Box**
> interface board. The CAN package published here (`caninput_can` / `caninput_msgs`) ships a
> **randomised, generic** build: its arbitration IDs, layout and scalings are illustrative
> placeholders, not the real hardware's CAN matrix.

---

## Windows setup (once, needed for every path)

1. **Install:** .NET 8 SDK, Docker Desktop, ATLAS 10 with a Stream Recorder.
2. **NuGet credentials** — the MA packages come from GitHub Packages, which needs a token
   even for public packages (a free GitHub account with a `read:packages` classic token is enough):
   ```powershell
   Copy-Item NuGet.Config.template NuGet.Config
   # edit NuGet.Config: fill in your GitHub username + read:packages token
   ```
3. **Build the bridge:**
   ```powershell
   cd windows\RosAtlasBridge
   dotnet build
   ```
   Restore fails with **401** but you've built before? Use the local cache:
   `dotnet restore --source "$env:USERPROFILE\.nuget\packages"` then `dotnet build --no-restore`.

**Every run, start in this order:** data source → foxglove_bridge → Kafka → Stream Recorder → bridge.

1. Kafka: `docker compose -f <your-kafka-compose>\docker-compose.yml up -d`
2. ATLAS: open it and **start the Stream Recorder**.
3. Bridge: `.\bin\Debug\net8.0\RosAtlasBridge.exe --robot ws://<source>:8765`
4. Stop with **Ctrl+C** (this closes the ATLAS session cleanly — killing the process leaves it "live" forever).

The bridge reconnects to the source every 2 s, so the Linux side may come up late — but topics
already publishing when the bridge connects get their true rates measured; late topics fall back
to 100 Hz. Start the source first.

---

## Quick start: simulation (no hardware)

Needs WSL 2 with Ubuntu 24.04, then: `sudo apt install ros-jazzy-ros-gz ros-jazzy-foxglove-bridge`

```bash
# WSL terminal 1 — a Gazebo demo
source /opt/ros/jazzy/setup.bash
ros2 launch ros_gz_sim_demos diff_drive.launch.py

# WSL terminal 2 — foxglove_bridge
source /opt/ros/jazzy/setup.bash
ros2 launch foxglove_bridge foxglove_bridge_launch.xml
```

Press **play** in Gazebo if the demo starts paused. Then on Windows:

```powershell
.\bin\Debug\net8.0\RosAtlasBridge.exe --robot ws://127.0.0.1:8765
```

(Use `127.0.0.1`, not `localhost` — WSL 2 forwards its ports, but `localhost` tries IPv6 first and loses ~2 s.)

Drive the car and watch the odometry move in ATLAS:
```bash
ros2 run teleop_twist_keyboard teleop_twist_keyboard --ros-args -r cmd_vel:=/model/vehicle_blue/cmd_vel
```
`i` forward, `,` back, `j`/`l` turn, `k` stop. The car keeps its last command; to halt it:
`ros2 topic pub --once /model/vehicle_blue/cmd_vel geometry_msgs/msg/Twist "{}"`.

**Demos worth streaming:** `diff_drive` (odometry, two vehicles), `imu`, `joint_states`,
`navsat` (GPS), `magnetometer`, `air_pressure`, `battery`. Avoid `depth_camera`, `gpu_lidar`,
`rgbd_camera` (broken upstream in Jazzy), and note camera/lidar topics are mostly large arrays,
which the bridge skips.

No WSL either? `tools/demo_publisher.py` publishes fake topics from any ROS 2 install.

---

## Quick start: CAN hardware

Needs a Raspberry Pi (Ubuntu 24.04, ROS 2 Jazzy) with a SocketCAN interface wired to the board,
plus: `sudo apt install python3-can ros-jazzy-foxglove-bridge`

**One-time:** copy the workspace to the Pi and build it (rebuild after changing the packages):
```bash
# on Windows, from this folder
scp -r ros2_ws/src <user>@<pi>:~/dev/caninput/ros2_ws/
# on the Pi
cd ~/dev/caninput/ros2_ws && source /opt/ros/jazzy/setup.bash && colcon build
```

> **The shipped decoder will not decode real hardware** — it's an illustrative example.
> For your own board, put a `_decoder_private.py` next to `decoder.py` exporting your real
> `DATA1`, `DATA2`, `NUM_*`, `bits` and `CanInputState`. The node picks it up automatically.

**Every run**, on the Pi (use `tmux` so SSH drops don't kill anything — detach `Ctrl+B, D`):

```bash
# 1. once per boot ("Device or resource busy" = already up, fine)
sudo ip link set can0 up type can bitrate 1000000

# 2. tmux window 1 — wait for "Receiving CAN frames (~1000/s)"
source /opt/ros/jazzy/setup.bash && source ~/dev/caninput/ros2_ws/install/setup.bash
ros2 run caninput_can caninput_node

# 3. tmux window 2 (sourcing both again — needed to describe the CAN message types)
ros2 launch foxglove_bridge foxglove_bridge_launch.xml
```

Then on Windows: `RosAtlasBridge.exe --robot ws://<pi-ip>:8765` (find the IP with `hostname -I`).

The node publishes `/caninput/inputs` (digitals, axes, rotaries, aux input) and
`/caninput/analog` (analogue channels, min/max errors, supply voltage) at 100 Hz, and `/caninput/status`
(board temp, rail voltage, LEDs, device/profile ID) at 1 Hz. Groups only republish when new CAN
frames update them, so a dead bus produces no fake samples.

**`windows/RosAtlasLauncher`** (`rosatlas`) automates this whole bring-up over one SSH session —
copy `bringup.json.template` to `bringup.json`, set `host` and `user`, and run it. Work in
progress (start-up race can cost topics their measured rate; nothing is checked on the Windows
side; CAN path only) — see its [README](windows/RosAtlasLauncher/README.md).

---

## How topics become ATLAS parameters

Everything is derived from the message definition — nothing is mapped by hand.

- **Numbers and bools** → one parameter each; `Time`/`Duration` → seconds; nested messages
  recurse with dotted paths (`pose.pose.position.x` → `pose_pose_position_x`).
- **Arrays** ≤ `maxArrayLength` (32) → one parameter per element; longer ones (images, lidar,
  covariances) and all **strings** are skipped. Topics with no numeric fields are ignored.
- **Naming:** topic `/model/vehicle_blue/odometry` → group `model_vehicle_blue_odometry`.
- **Sample time:** `header.stamp` when it's real wall-clock time; otherwise foxglove's receive
  time (normal for simulation time); last resort the Windows clock.
- **Rates:** measured per topic in a 2 s discovery window after connecting; later topics get 100 Hz.
- **Layout is fixed by each topic's first message**; fields missing later arrive as *missing* samples.
- **Session:** one per bridge run (`ROS yyyy-MM-dd HH:mm:ss`), written to streams `""` and
  `"Stream1"`; every ROS message becomes one single-sample `PeriodicData` packet at its own
  timestamp, so irregular timing is preserved. Reconnects continue the same session.

## Configuration: `windows/RosAtlasBridge/config.json`

| Key | Default | Meaning |
|---|---|---|
| `robotUrl` | `ws://robot.local:8765` | foxglove_bridge address (override: `--robot`) |
| `kafkaBroker` | `localhost:9094` | Broker the Stream Recorder reads from |
| `dataSource` / `streams` / `sessionName` | `Default` / `["", "Stream1"]` / `ROS` | ATLAS session placement |
| `includeTopics` / `excludeTopics` | `.*` / rosout, parameter_events, tf | Topic filters (regex) |
| `maxArrayLength` | `32` | Longer arrays are skipped |
| `discoveryWindowSeconds` / `flushIntervalMs` | `2.0` / `50` | Rate measurement / write cadence |
| `defaultFrequencyHz`, `defaultMinValue`, `defaultMaxValue` | `100`, `0`, `100` | Fallbacks |
| `parameters` | CAN board entries | Per-parameter `units`, `minValue`, `maxValue`, `description`, `formatString` |

`parameters` keys are `<topic>/<field path>`, `*` is a wildcard:
```json
"/model/vehicle_*/odometry/pose.pose.position.*": { "units": "m", "minValue": -10, "maxValue": 10 }
```
The file is read at start-up — restart the bridge after editing.

**Command line:** `RosAtlasBridge.exe [config.json] [--robot ws://host:8765] [--dry-run] [--seconds N]`
— `--dry-run` prints what *would* reach ATLAS without touching Kafka (use it first with any new
source); `--seconds N` stops cleanly after N seconds.

---

## Troubleshooting

| Symptom | Cause / fix |
|---|---|
| Parameters listed but **no values** in ATLAS | The recorder needs `PeriodicData` on a named stream. Keep `streams` as `["", "Stream1"]`. |
| One topic **stale** while others work | Declared rate far from real rate — it appeared after discovery and got 100 Hz. Start the source before the bridge; check the `Defined ... at ~N Hz` log line. |
| `Connection to ws://…:8765 failed` repeating | foxglove_bridge not running, or the IP changed (`hostname -I` on the source). The bridge keeps retrying — no restart needed. |
| `No CAN frames on 'can0'` | Board unpowered, `can0` down, or wrong bitrate: `ip -details link show can0`. |
| `header.stamp is not wall-clock time` warning | Normal for simulation time; receive time is used instead. |
| Session stays "live" in ATLAS | The bridge was killed instead of stopped with Ctrl+C. |
| Empty sessions in ATLAS | The session is created at start-up even if the source is never reached. |
| `dotnet restore` 401 | Feed token expired — restore from the local cache (see setup) or refresh the token. |
| A Gazebo demo publishes nothing | Paused, or one of the broken camera/lidar demos. Check `ros2 topic hz <topic>`. |

## Repository layout & limitations

```
ros2_ws/src/                 caninput_msgs + caninput_can (built on the Pi)
windows/RosAtlasBridge/      the bridge (C#, .NET 8): Foxglove client, .msg/CDR decoder,
                             topic→parameter mapping, ATLAS session writer, config.json
windows/RosAtlasLauncher/    `rosatlas` one-command SSH bring-up (work in progress)
tools/demo_publisher.py      fake topics for testing without hardware
```

Known limitations: the session is created before the source connects (empty sessions possible);
late topics get the default rate; simulation time is replaced by receive time; array elements are
named by position; quaternions stay raw `x/y/z/w`; strings are dropped (no ATLAS events yet); one
source per bridge process. Ideas: MCAP/rosbag2 import, automatic units for standard types, a
desktop UI.
