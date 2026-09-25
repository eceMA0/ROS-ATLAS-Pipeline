# rosatlas - pipeline launcher

One command to bring up the whole ROS -> ATLAS pipeline, replacing the manual
sequence of SSH sessions.

## Usage

```
rosatlas 192.168.1.42
rosatlas --host robot.local --user pi
rosatlas 192.168.1.42 --no-bridge
```

If no host is given on the command line or in `bringup.json`, it prompts for one.

## What it does

Everything robot-side runs inside **one SSH session executing one script**, so you
authenticate at most once:

1. Prompts for your sudo password (press Enter to skip if sudo is passwordless).
2. Opens a single SSH session. Answer any host-key or SSH password prompt normally -
   they are left attached to your terminal.
3. Brings up CAN (`ip link set can0 up type can bitrate 1000000`), skipping it if the
   interface is already up.
4. Optionally runs `colcon build --symlink-install` (`--build`).
5. Launches `foxglove_bridge` and `caninput_node`, output prefixed `[foxglove]` / `[caninput]`.
6. Waits for the WebSocket port, checked *on the robot* so a firewall cannot cause a
   false "did not start".
7. Starts `RosAtlasBridge.exe --robot ws://<host>:8765`.

Ctrl+C tears everything down in reverse so `RosAtlasBridge` can write its
`EndOfSession` packet and ATLAS does not show the session as live forever.

### Cleanup is guaranteed by the robot, not the laptop

The remote script installs a `trap ... EXIT INT TERM HUP` and starts each node with
`setsid`, in its own process group. If the network drops, your laptop sleeps, or
`rosatlas.exe` is killed outright, the robot still kills its own nodes - it does not
depend on us reconnecting to clean up.

## Requirements

- **Windows OpenSSH client** (`ssh.exe`) - built into Windows 10/11. The launcher
  shells out to it, so your existing keys, `~/.ssh/config` and `known_hosts` all apply.
- **SSH auth by key or password.** Password login works: ssh's own prompt is left
  attached to your terminal rather than captured. Keys are still smoother.
- **A sudo password**, prompted for (masked) before connecting. The robot tries
  `sudo -n` first, so with a NOPASSWD rule or a live sudo timestamp you can just press
  Enter. For unattended runs set `ROSATLAS_SUDO_PASSWORD`, or add a rule:
  ```
  echo "$USER ALL=(ALL) NOPASSWD: /usr/sbin/ip" | sudo tee /etc/sudoers.d/rosatlas-can
  ```
- **Kafka running locally** (`localhost:9094`) - the bridge needs it, the launcher does not.

## Troubleshooting

`--dump-script` prints the exact bash that would run on the robot, without connecting.
Pipe it to `bash -n` to syntax-check it, or paste it into an SSH session to debug
the robot side directly.

To check for leftover processes:
```
ssh <user>@<robot> "pgrep -a -f '[f]oxglove_bridge|[c]aninput_node'"
```
The `[f]` bracket trick matters: a plain `pkill -f 'foxglove_bridge'` also matches the
command line of the shell running it, so it kills itself before finishing.

## Configuration

Copy `bringup.json.template` to `bringup.json` (same folder) first — the template
ships publicly, the copy is git-ignored because it holds your robot's address.
Every field is commented there. The values most likely to need changing are
`host`, `user`, `rosDistro`, `workspace` and `canChannel`. Command-line flags
override the file.

## Assumptions made

These were inferred from source comments, as the manual steps were not documented:

- ROS 2 Jazzy is at `/opt/ros/jazzy` and the overlay at `<workspace>/install/setup.bash`
  (sourced only if present).
- `foxglove_bridge_launch.xml` accepts a `port:=` argument.
- `foxglove_bridge` and `caninput_node` are the only robot-side processes needed.
- CAN is left up on exit (use `--can-down` to change that), since other users may
  be on the same bus.

Correct any of these in `bringup.json` or in `RemoteScript.cs`.
