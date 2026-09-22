# ROS-ATLAS-Pipeline

Stream live ROS 2 data into **ATLAS** through Open Streaming.

The pipeline is **robot-independent**. Nothing in the bridge is written for a specific message type:
it reads each topic's ROS message definition at runtime and turns every numeric field into an
ATLAS parameter. Any ROS 2 source that can run `foxglove_bridge` works without code changes. It has
been used with:

- **CAN input board**: real CAN hardware read by a Raspberry Pi running ROS 2 Jazzy
- **Gazebo Harmonic** simulations (the `ros_gz_sim_demos`) running in WSL

> **About the CAN example** — the demo video was recorded against a real **SIU-700 / TAG-Box**
> interface board. The CAN package published here (`caninput_can` / `caninput_msgs`) ships a
> **randomised, generic** build: the device name, I/O layout and decoder values are illustrative
> placeholders, not the real hardware's CAN matrix.

---

## How it works

```
 ROBOT / SIMULATION (Linux, ROS 2 Jazzy)                    WINDOWS
 ─────────────────────────────────────────                  ──────────────────────────────────────────

 CAN board ──CAN 1 Mbit──► can0 ──► caninput_node ─┐
                                               ├─ ROS 2 topics ─► foxglove_bridge
 Gazebo ──► ros_gz_bridge ─────────────────────┘                   │
                                                                   │ WebSocket :8765
                                                                   │ (topic list + message
                                                                   │  definitions + raw CDR bytes)
                                                                   ▼
                                                           RosAtlasBridge.exe
                                                                   │ Open Streaming packets
                                                                   ▼
                                                           Kafka (localhost:9094)
                                                                   │
                                                                   ▼
                                                           ATLAS Stream Recorder ─► live session
```

| Stage | Runs on | Role |
|---|---|---|
| **Data source** | Robot / WSL | Anything that publishes ROS 2 topics. For the CAN board that is `caninput_node`; for Gazebo it is `ros_gz_bridge`. |
| **foxglove_bridge** | Same machine as the source | Standard ROS 2 package. Serves every topic over a WebSocket: the topic list, each topic's full `.msg` definition, and the raw message bytes. It lets a plain Windows .NET program read ROS without a ROS install. |
| **RosAtlasBridge** | Windows | Subscribes over the WebSocket, decodes messages using their definitions, flattens them into numbers and writes them to Kafka as an ATLAS session. |
| **Kafka** | Windows (Docker) | The broker ATLAS's Stream Recorder reads Open Streaming data from. |
| **ATLAS** | Windows | Stream Recorder must be running to capture the live session. |

The data that reaches Windows is real ROS data: the same CDR bytes ROS uses internally. It is not a
CSV export or a custom format.

---

## Repository layout

```
ROS-ATLAS-Pipeline/
├── ros2_ws/src/                    ROS 2 workspace (built on the robot)
│   ├── caninput_msgs/              Message definitions: CanInputInputs, CanInputAnalog, CanInputStatus
│   └── caninput_can/               caninput_node: SocketCAN -> decode -> publish
├── windows/
│   ├── RosAtlasBridge/             The bridge (C#, .NET 8)
│   │   ├── Foxglove/               WebSocket client for the foxglove protocol
│   │   ├── Ros/                    .msg schema parser, CDR decoder, message flattener
│   │   ├── Bridge/                 Topic -> ATLAS group/parameter mapping, naming, dry-run sink
│   │   ├── Atlas/                  ATLAS session writer (MA Support Library)
│   │   └── config.json             Bridge settings and per-parameter metadata
│   └── RosAtlasLauncher/           `rosatlas` one-command SSH bring-up (work in progress)
├── tools/
│   └── demo_publisher.py           Fake inputs + IMU topics for testing without hardware
```

---

## From ROS messages to ATLAS parameters

No code maps a topic to parameters by hand. Everything is derived from the message definition.

### Flattening rules
Implemented in `windows/RosAtlasBridge/Ros/MessageFlattener.cs`:

| ROS field | ATLAS result |
|---|---|
| Numbers (`float64`, `int32`, `uint8`, …) | One parameter each |
| `bool` | One parameter, 0 / 1 |
| `builtin_interfaces/Time`, `Duration` | One parameter in seconds |
| Nested messages | Recurse; the path joins names with dots (`pose.pose.position.x`) |
| Arrays ≤ `maxArrayLength` (32) | One parameter per element (`clutch_percent[0]`) |
| Longer arrays (images, point clouds, 36-element covariances, lidar ranges) | Skipped |
| `string` | Skipped (ATLAS parameters are numeric) |
| Top-level `std_msgs/Header` | Its `stamp` becomes the sample time, not a parameter |

A topic with no numeric fields at all (e.g. `std_msgs/String`) is ignored.

### Naming
| ROS | ATLAS |
|---|---|
| Topic `/model/vehicle_blue/odometry` | Group `model_vehicle_blue_odometry` |
| Field `pose.pose.position.x` | Parameter `pose_pose_position_x` |
| Identifier | `pose_pose_position_x:model_vehicle_blue_odometry` |

### Timing
- **Sample time:**
  - Uses `header.stamp` when it is real clock time (after 2000-01-01).
  - Otherwise (no header, or simulation time) uses foxglove_bridge's receive time.
  - As a last resort, uses the Windows clock.
- **Parameter rate:**
  - Measured per topic during a discovery window (`discoveryWindowSeconds`, default 2 s) that starts when the WebSocket connects.
  - Topics first seen after the window get `defaultFrequencyHz` (100).
- **Parameter layout:** fixed by each topic's first message. Fields that a later message is missing (a shorter array) are sent as *missing* samples.

### Session structure
The writer follows the sequence of FormulaStudent-Atlas-Example's `MockDataWriter`:

1. **One session** per bridge run, named `ROS yyyy-MM-dd HH:mm:ss`, data source `Default`.
2. **Two streams:** everything is written to the default stream `""` and to `"Stream1"`.
3. **Configuration packets:**
   - One packet declares all topics found during discovery.
   - A topic that appears later gets its own configuration packet.
4. **Data:** every ROS message becomes one single-sample `PeriodicData` packet at its timestamp.
5. **Reconnects:** if the robot drops, the bridge reconnects every 2 s and continues **the same session**.
6. **End:** Ctrl+C sends `EndOfSession`. If you kill the process instead, ATLAS shows the session as live forever.

> ATLAS also adds its own **Session Details** group to every session. The bridge does not create it.

---

## Configuration: `windows/RosAtlasBridge/config.json`

| Key | Default | Meaning |
|---|---|---|
| `robotUrl` | `ws://robot.local:8765` | foxglove_bridge address. Override with `--robot`. |
| `kafkaBroker` | `localhost:9094` | Broker the ATLAS Stream Recorder reads from |
| `dataSource` / `streams` / `sessionName` | `Default` / `["", "Stream1"]` / `ROS` | ATLAS session placement |
| `includeTopics` / `excludeTopics` | `.*` / `/rosout`, `/parameter_events`, `/tf`, `/tf_static` | Topic filters (regex) |
| `maxArrayLength` | `32` | Longer arrays are skipped |
| `discoveryWindowSeconds` | `2.0` | How long rates are measured after connecting |
| `flushIntervalMs` | `50` | How often rows are written |
| `defaultFrequencyHz`, `defaultMinValue`, `defaultMaxValue` | `100`, `0`, `100` | Used when nothing better is known |
| `parameters` | CAN board entries | Optional per-parameter `units`, `minValue`, `maxValue`, `description`, `formatString` |

`parameters` keys are `<topic>/<ROS field path>`, with `*` as a wildcard:

```json
"parameters": {
  "/caninput/inputs/clutch_percent[*]": { "units": "%", "minValue": 0, "maxValue": 100 },
  "/model/vehicle_*/odometry/pose.pose.position.*": { "units": "m", "minValue": -10, "maxValue": 10 }
}
```

The file is read at start-up, so restart the bridge after editing it.

### Command line

```
RosAtlasBridge.exe [config.json] [--robot ws://host:8765] [--dry-run] [--seconds N]
```

- **`--dry-run`:** prints the groups, parameters, rates and sample values that *would* be sent, without touching Kafka. Use it first with any new robot.
- **`--seconds N`:** stops cleanly after N seconds.

---

## Prerequisites

**Windows**
- .NET 8 SDK
- Docker Desktop, running the Kafka stack ATLAS uses (broker on `localhost:9094`)
- ATLAS 10 with a Stream Recorder
- Access to the Motion Applied NuGet packages (`MA.DataPlatforms.Streaming.Support.Lib.Core` 2.1.4.22, `MA.Streaming.*` 2.1.4.14)

**Robot (CAN board path)**
- Raspberry Pi with Ubuntu 24.04 and ROS 2 Jazzy
- A SocketCAN interface (`can0`) wired to the CAN input board
- `sudo apt install python3-can ros-jazzy-foxglove-bridge`

**Simulation (Gazebo path)**
- WSL 2 with Ubuntu 24.04 and ROS 2 Jazzy
- `sudo apt install ros-jazzy-ros-gz ros-jazzy-foxglove-bridge`

---

## Running: CAN input hardware

### One-time Pi setup
From Windows, copy the workspace sources to the Pi, then build on the Pi:

```bash
# on Windows (PowerShell or Git Bash), from this folder
scp -r ros2_ws/src <user>@robot.local:~/dev/caninput/ros2_ws/

# on the Pi
cd ~/dev/caninput/ros2_ws
source /opt/ros/jazzy/setup.bash
colcon build
```

Rebuild whenever `caninput_msgs` or `caninput_can` change.

### Every run: Pi (over SSH)

**1. CAN interface**, once per boot:
```bash
sudo ip link set can0 up type can bitrate 1000000
```
`RTNETLINK answers: Device or resource busy` means it is already up. That is fine.

**2. Terminal 1: CAN node**
```bash
tmux
source /opt/ros/jazzy/setup.bash
source ~/dev/caninput/ros2_ws/install/setup.bash
ros2 run caninput_can caninput_node
```
Wait for `Receiving CAN frames (~1000/s)`.

The node publishes:

| Topic | Type | Rate |
|---|---|---|
| `/caninput/inputs` | `caninput_msgs/CanInputInputs`: 18 digitals, 2 clutch paddles, 11 rotaries, AN1 | 100 Hz |
| `/caninput/analog` | `caninput_msgs/CanInputAnalog`: AN2–AN12, min/max errors, VBATT | 100 Hz |
| `/caninput/status` | `caninput_msgs/CanInputStatus`: board temperature, 5 V rail, LEDs, wheel/driver ID | 1 Hz |

Node parameters: `channel` (`can0`), `interface` (`socketcan`), `publish_rate_hz` (100), `status_rate_hz` (1), `frame_id`.
A group is only republished when new CAN frames have updated it, so a dead bus produces no fake samples.

> **Note — the shipped CAN decoder uses example values.** `caninput_can/decoder.py`
> contains an illustrative CAN map (placeholder arbitration IDs, bit offsets and
> scaling) so you can study the SocketCAN -> decode -> publish flow. **These
> placeholders will not decode any real device.** To decode your own hardware,
> add a `_decoder_private.py` module next to `decoder.py` that exports the real
> `DATA1`, `DATA2`, `NUM_DIGITAL`, `NUM_ANALOG`, `NUM_ROTARY`, `bits` and
> `CanInputState` definitions for your board. When that file is present the node
> imports it automatically and uses your real map; when it is absent the node
> falls back to the built-in example. Nothing else needs to change.

**3. Terminal 2: foxglove_bridge**
```bash
tmux
source /opt/ros/jazzy/setup.bash
source ~/dev/caninput/ros2_ws/install/setup.bash   # needed so the CAN message types can be described
ros2 launch foxglove_bridge foxglove_bridge_launch.xml
```

`tmux` keeps both processes alive if SSH drops. Detach with **Ctrl+B, D**; reattach with `tmux attach`.
Find the Pi's address with `hostname -I`.

Then follow [Running: Windows side](#running-windows-side) with `--robot ws://<pi-ip>:8765`.

---

## Running: Gazebo simulation (WSL)

```bash
# terminal 1: a demo (pick one; see the list below)
source /opt/ros/jazzy/setup.bash
ros2 launch ros_gz_sim_demos diff_drive.launch.py

# terminal 2: foxglove_bridge
source /opt/ros/jazzy/setup.bash
ros2 launch foxglove_bridge foxglove_bridge_launch.xml
```

Press **play** in Gazebo if the demo starts paused.

Then follow [Running: Windows side](#running-windows-side) with `--robot ws://127.0.0.1:8765`.
WSL 2 forwards its ports to Windows. `127.0.0.1` connects immediately, while `localhost` first tries IPv6 and loses about 2 s.

**Demos that suit ATLAS:**

| Demo | Useful data |
|---|---|
| `diff_drive.launch.py` | Odometry for two vehicles (`vehicle_blue` odometry publishes at 1 Hz) |
| `imu.launch.py` | Acceleration, angular velocity, orientation |
| `joint_states.launch.py` | Joint positions, velocities, efforts |
| `navsat.launch.py` | GPS latitude / longitude / altitude |
| `magnetometer.launch.py`, `air_pressure.launch.py`, `battery.launch.py` | Field, pressure, battery state |

- **Broken in Jazzy:** `depth_camera`, `gpu_lidar` and `rgbd_camera`. Their world file is commented out upstream, so they publish nothing.
- **Little to show:** camera and lidar topics are mostly large arrays, which the bridge skips.

**Driving the car from the keyboard:**
```bash
ros2 run teleop_twist_keyboard teleop_twist_keyboard --ros-args -r cmd_vel:=/model/vehicle_blue/cmd_vel
```
- **Keys:** `i` forward, `,` back, `j`/`l` turn, `k` stop.
- **Stop it:** the car keeps its last command, so to stop it send `ros2 topic pub --once /model/vehicle_blue/cmd_vel geometry_msgs/msg/Twist "{}"`.

---

## Running: Windows side

**1. Kafka**
```powershell
docker compose -f <path-to-your-kafka-compose>\docker-compose.yml up -d
```

**2. ATLAS:** open it and **start the Stream Recorder first**.

**3. Build** (first time and after code changes):

The MA.Streaming / MA.DataPlatforms packages come from GitHub Packages
(`nuget.pkg.github.com/mat-docs`), which needs authentication even for public
packages. Set up your feed credentials once:
```powershell
Copy-Item NuGet.Config.template NuGet.Config
# then edit NuGet.Config and put in your GitHub username + a read:packages token
```
`NuGet.Config` is git-ignored so your token is never committed. Then:
```powershell
cd windows\RosAtlasBridge
dotnet build
```
If restore fails with **401** but the packages are already in your local cache:
```powershell
dotnet restore --source "$env:USERPROFILE\.nuget\packages"
dotnet build --no-restore
```

**4. Run**
```powershell
.\bin\Debug\net8.0\RosAtlasBridge.exe --robot ws://<robot>:8765
```

Expected output:
```
WebSocket connected (foxglove.sdk.v1)
Subscribing to /caninput/inputs (caninput_msgs/msg/CanInputInputs)
...
Defined /caninput/inputs: 34 parameters in group 'caninput_inputs' at ~100 Hz
```

**5. Stop** with **Ctrl+C**, so the session gets `EndOfSession`.

### Start order
**Source → foxglove_bridge → Kafka → Stream Recorder → RosAtlasBridge**

- **Robot side can come up later:** the bridge retries every 2 s.
- **But start the data source before the bridge:** its topics are then measured during discovery instead of receiving the 100 Hz default.

---

## RosAtlasLauncher (`rosatlas`): work in progress

`windows/RosAtlasLauncher` is a one-command bring-up for the CAN board path. Over SSH it:
1. brings up `can0`
2. starts `caninput_node` and foxglove_bridge
3. waits for port 8765
4. starts RosAtlasBridge

See its own [README](windows/RosAtlasLauncher/README.md).

It has **not been validated end-to-end yet**. Known gaps before relying on it:

- **Unclean stop:** the bridge may be force-killed on shutdown without sending `EndOfSession`.
- **Race at start-up:** the bridge starts once the port is open, not once the CAN topics exist, so slow topics can get the default rate.
- **Wrong defaults:** `bringup.json` has user `ubuntu` and workspace `~/ROS-ATLAS-Pipeline/ros2_ws`. Set them for your Pi.
- **Nothing checked on Windows:** Kafka and the Stream Recorder are not checked.
- **CAN-only:** there is no Gazebo profile.

---

## Development

### Without hardware
- **`tools/demo_publisher.py`** publishes fake inputs and status, an IMU and a text topic (which should be ignored).
- **`--dry-run`** on the bridge shows exactly what would reach ATLAS.

---

## Troubleshooting

| Symptom | Cause / fix |
|---|---|
| ATLAS lists parameters but shows **no values** | The recorder must see `PeriodicData` on a named stream. Keep `streams` as `["", "Stream1"]`. |
| One topic **stale / no values** while others work | Its declared rate is far from its real rate. It was probably first seen after discovery and got 100 Hz. Start the source before the bridge. Check the rate the bridge logs with `Defined ... at ~N Hz`. |
| `Connection to ws://…:8765 failed` repeating | foxglove_bridge is not running, or the IP changed. Run `hostname -I` on the robot. The bridge keeps retrying, so no restart is needed. |
| `No CAN frames on 'can0'` | Board not powered, `can0` not up, or wrong bitrate. Check with `ip -details link show can0`. |
| `header.stamp is not wall-clock time` warning | Normal for simulation time. The receive time is used instead. |
| Session stays "live" in ATLAS | The bridge was killed instead of stopped with Ctrl+C. |
| Empty sessions in ATLAS | The bridge creates the session at start-up, even if the robot is never reached. |
| `dotnet restore` 401 | NuGet feed token expired. Restore from the local cache (see Build above) or refresh the token. |
| A Gazebo demo publishes nothing | The demo may be paused, or it is one of the broken camera/lidar demos listed above. Check with `ros2 topic hz <topic>`. |

---

## Limitations and next steps

- **Session created too early.** The session starts before the robot connects. It should wait for the first data.
- **Topics that appear late** get a default rate. Per-topic rate measurement is planned.
- **Simulation time** is replaced by receive time rather than mapped onto the session clock.
- **Arrays are named by position** (`transforms[0]`, `position[3]`), not by the names inside the message. If a publisher reorders elements, values land under the wrong names.
- **Quaternions** are passed through as raw `x/y/z/w`; there is no roll/pitch/yaw.
- **Strings are dropped.** ROS log messages and state changes are not yet sent as ATLAS events.
- **One robot per bridge process / session.**
- **Ideas:**
  - MCAP / rosbag2 import into ATLAS sessions (the same decoder applies)
  - automatic units for standard message types
  - a desktop UI replacing the terminal workflow



# ROS-ATLAS-Pipeline
