using System.Globalization;

namespace RosAtlasLauncher;

/// <summary>
/// Builds the single bash script that performs the entire robot-side bring-up.
///
/// WHY ONE SCRIPT INSTEAD OF SEVERAL SSH CALLS
/// Every ssh invocation is a separate authentication. With key auth that is merely wasteful; with
/// password auth it is fatal, because we redirect stdout to capture output and ssh's own password
/// prompt disappears into that buffer, leaving the user staring at "Permission denied". It also
/// means a cleanup command is a fresh connection that can itself fail to authenticate, which is
/// how ROS nodes were being left running on the robot.
///
/// One session fixes both. It also gives us something better than a remote kill command: the
/// script traps its own exit and kills its children, so if the network drops, the laptop sleeps,
/// or the process is killed, the robot still cleans itself up.
///
/// The script literals below are raw interpolated strings where interpolation is written as
/// {{expr}} and a single brace stays literal, which is what bash needs.
/// </summary>
public static class RemoteScript
{
    /// <summary>
    /// Quotes a path that may begin with "~". Single quotes suppress tilde expansion, so the
    /// leading ~/ is swapped for "$HOME"/ - quoted enough to survive spaces, unquoted where it
    /// must expand.
    /// </summary>
    private static string QuotePath(string path) =>
        path.StartsWith("~/", StringComparison.Ordinal)
            ? "\"$HOME\"/" + Shell.Quote(path[2..])
            : Shell.Quote(path);

    /// <summary>
    /// Formats a double so it always carries a decimal point.
    ///
    /// ROS 2 parameters are strictly typed and inferred from the literal text: "100" is an INTEGER
    /// and is rejected for a parameter declared DOUBLE, with
    /// "Trying to set parameter 'publish_rate_hz' to '100' of type 'INTEGER'". Plain ToString on
    /// 100.0 yields "100", so force at least one decimal place.
    /// </summary>
    private static string RosDouble(double value) =>
        value.ToString("0.0###########", CultureInfo.InvariantCulture);

    public static string Build(LauncherConfig config)
    {
        var channel = Shell.Quote(config.CanChannel);
        var setupScript = Shell.Quote($"/opt/ros/{config.RosDistro}/setup.bash");
        var overlay = QuotePath($"{config.Workspace.TrimEnd('/')}/install/setup.bash");
        var publishRate = RosDouble(config.PublishRateHz);
        var statusRate = RosDouble(config.StatusRateHz);

        // set -u is deliberately omitted: the ROS setup scripts reference unset variables.
        return $$"""
        set -e

        # There is no terminal on this session, so make Python (caninput_node, ros2 launch) and ROS
        # logging flush per line instead of holding output back in a buffer.
        export PYTHONUNBUFFERED=1
        export RCUTILS_LOGGING_BUFFERED_STREAM=1

        CAN_CHANNEL={{channel}}
        FOXGLOVE_PORT={{config.FoxglovePort}}
        PIDS=""

        # Kill everything we started, however we exit. This is the guarantee that a dropped
        # connection cannot leave ROS nodes driving the CAN bus.
        cleanup() {
            trap - EXIT INT TERM HUP
            for pid in $PIDS; do
                # Negative pid targets the whole process group, catching the children that
                # "ros2 launch" spawns - they do not die with their supervisor.
                kill -TERM -"$pid" 2>/dev/null || kill -TERM "$pid" 2>/dev/null || true
            done
            for _ in 1 2 3 4 5; do
                still=""
                for pid in $PIDS; do
                    kill -0 "$pid" 2>/dev/null && still="yes"
                done
                [ -z "$still" ] && break
                sleep 1
            done
            for pid in $PIDS; do
                kill -KILL -"$pid" 2>/dev/null || kill -KILL "$pid" 2>/dev/null || true
            done
            {{CanDownStep(config)}}
            echo "__ROSATLAS_STOPPED__"
        }
        trap cleanup EXIT INT TERM HUP

        # --- sudo ---------------------------------------------------------------------------
        # The launcher sends the password as the first line of stdin, or an empty line if it
        # believes sudo is passwordless. -S makes sudo read from there; -p '' suppresses its
        # prompt so it cannot be mistaken for output.
        IFS= read -r SUDO_PASSWORD || true

        run_root() {
            if sudo -n true 2>/dev/null; then
                sudo -n "$@"
            else
                printf '%s\n' "$SUDO_PASSWORD" | sudo -S -p '' "$@"
            fi
        }

        # --- CAN ----------------------------------------------------------------------------
        if ! ip link show "$CAN_CHANNEL" >/dev/null 2>&1; then
            echo "__ROSATLAS_ERROR__ CAN interface '$CAN_CHANNEL' does not exist on this robot."
            exit 1
        fi

        if ip -details -brief link show "$CAN_CHANNEL" | grep -qw 'UP'; then
            echo "__ROSATLAS_LOG__ CAN $CAN_CHANNEL already up."
        else
            if ! run_root ip link set "$CAN_CHANNEL" up type can bitrate {{config.CanBitrate}} 2>&1; then
                echo "__ROSATLAS_ERROR__ Could not bring up $CAN_CHANNEL (wrong sudo password, or the bus is busy)."
                exit 1
            fi
            echo "__ROSATLAS_LOG__ CAN $CAN_CHANNEL up at {{config.CanBitrate}} bit/s."
        fi

        # --- ROS environment ----------------------------------------------------------------
        if [ ! -f {{setupScript}} ]; then
            echo "__ROSATLAS_ERROR__ {{setupScript}} not found. Is ROS 2 {{config.RosDistro}} installed?"
            exit 1
        fi
        . {{setupScript}}
        {{BuildStep(config)}}
        if [ -f {{overlay}} ]; then
            . {{overlay}}
        else
            # Do not carry on regardless: without the overlay, "ros2 run caninput_can" fails with a
            # confusing "Package not found" well after we have already brought the CAN bus up.
            # Look for the workspace so the message can name the real path.
            FOUND=$(find "$HOME" -maxdepth 5 -type d -name install -path '*ros2_ws*' 2>/dev/null | head -1)
            if [ -z "$FOUND" ]; then
                FOUND=$(find "$HOME" -maxdepth 6 -type d -name caninput_can 2>/dev/null | head -1)
                if [ -n "$FOUND" ]; then
                    echo "__ROSATLAS_ERROR__ Found the caninput_can sources at $FOUND but no build output. Run rosatlas with --build, or build it on the robot with 'colcon build'."
                    exit 1
                fi
                echo "__ROSATLAS_ERROR__ No ROS 2 workspace found under $HOME. Set \"workspace\" in bringup.json to the directory containing src/caninput_can."
                exit 1
            fi
            echo "__ROSATLAS_ERROR__ Overlay not found at the configured path, but a workspace exists at ${FOUND%/install}. Set \"workspace\" in bringup.json to that path."
            exit 1
        fi

        # Confirm the package is actually reachable before we start anything. Failing here is far
        # clearer than a node dying seconds later for the same reason.
        if ! ros2 pkg prefix caninput_can >/dev/null 2>&1; then
            echo "__ROSATLAS_ERROR__ Package 'caninput_can' is not on the ROS package path even after sourcing the overlay. Try rebuilding with --build."
            exit 1
        fi

        if ! ros2 pkg prefix foxglove_bridge >/dev/null 2>&1; then
            echo "__ROSATLAS_ERROR__ Package 'foxglove_bridge' is not installed. On the robot: sudo apt install ros-{{config.RosDistro}}-foxglove-bridge"
            exit 1
        fi

        # --- clear stale nodes --------------------------------------------------------------
        # A previous run that was killed abruptly (or an ssh session that died before its cleanup
        # trap ran) can leave foxglove_bridge holding port $FOXGLOVE_PORT, which then fails with
        # "Couldn't initialize websocket server: Bind Error". Reclaim it before starting.
        # The [f] bracket keeps the pattern from matching this script's own command line.
        STALE=$(pgrep -f '[f]oxglove_bridge|[c]aninput_node' 2>/dev/null || true)
        if [ -n "$STALE" ]; then
            echo "__ROSATLAS_LOG__ Clearing stale ROS nodes from a previous run ..."
            pkill -f '[f]oxglove_bridge|[c]aninput_node' 2>/dev/null || true
            for _ in 1 2 3 4 5; do
                pgrep -f '[f]oxglove_bridge|[c]aninput_node' >/dev/null 2>&1 || break
                sleep 1
            done
            pkill -9 -f '[f]oxglove_bridge|[c]aninput_node' 2>/dev/null || true
            sleep 1
        fi

        # The port can stay bound briefly after the process dies; wait rather than fail outright.
        for _ in 1 2 3 4 5; do
            if command -v ss >/dev/null 2>&1; then
                ss -ltn 2>/dev/null | grep -q ":$FOXGLOVE_PORT " || break
            else
                break
            fi
            sleep 1
        done

        if command -v ss >/dev/null 2>&1 && ss -ltn 2>/dev/null | grep -q ":$FOXGLOVE_PORT "; then
            echo "__ROSATLAS_ERROR__ Port $FOXGLOVE_PORT on the robot is already in use by something we did not start. Find it with: sudo ss -ltnp | grep $FOXGLOVE_PORT"
            exit 1
        fi

        # --- nodes --------------------------------------------------------------------------
        # setsid puts each node in its own process group so cleanup() can signal the whole tree.
        # Output goes through a process substitution, not a pipe: after "cmd | sed &", $! is sed's
        # pid, so cleanup would signal sed and the readiness check would watch sed, not the node.
        setsid ros2 launch foxglove_bridge foxglove_bridge_launch.xml port:=$FOXGLOVE_PORT > >(sed -u 's/^/[foxglove] /') 2>&1 &
        FOXGLOVE_PID=$!
        PIDS="$PIDS $FOXGLOVE_PID"

        setsid ros2 run caninput_can caninput_node --ros-args -p channel:=$CAN_CHANNEL -p publish_rate_hz:={{publishRate}} -p status_rate_hz:={{statusRate}} > >(sed -u 's/^/[caninput] /') 2>&1 &
        CANINPUT_PID=$!
        PIDS="$PIDS $CANINPUT_PID"

        # --- readiness ----------------------------------------------------------------------
        # Checked on the robot rather than from Windows, because a firewall can hide a listening
        # port from outside and we would report a start-up failure that did not happen.
        ready=""
        i=0
        while [ $i -lt {{config.ReadyTimeoutSeconds}} ]; do
            i=$((i + 1))
            if ! kill -0 "$FOXGLOVE_PID" 2>/dev/null; then
                echo "__ROSATLAS_ERROR__ foxglove_bridge exited during start-up; see the [foxglove] lines above."
                exit 1
            fi
            if ! kill -0 "$CANINPUT_PID" 2>/dev/null; then
                echo "__ROSATLAS_ERROR__ caninput_node exited during start-up; see the [caninput] lines above."
                exit 1
            fi
            if command -v ss >/dev/null 2>&1; then
                ss -ltn 2>/dev/null | grep -q ":$FOXGLOVE_PORT " && ready="yes"
            else
                (exec 3<>/dev/tcp/127.0.0.1/$FOXGLOVE_PORT) 2>/dev/null && ready="yes"
            fi
            [ -n "$ready" ] && break
            sleep 1
        done

        if [ -z "$ready" ]; then
            echo "__ROSATLAS_ERROR__ foxglove_bridge did not open port $FOXGLOVE_PORT in time."
            exit 1
        fi

        echo "__ROSATLAS_READY__"

        # Block until the launcher closes stdin or the connection dies; then the EXIT trap runs.
        while IFS= read -r _; do :; done
        """;
    }

    private static string BuildStep(LauncherConfig config)
    {
        if (!config.Build)
        {
            return ":";
        }

        var workspace = QuotePath(config.Workspace);
        return $$"""
        echo "__ROSATLAS_LOG__ Building the workspace ..."
        if ! ( cd {{workspace}} && colcon build --symlink-install ) 2>&1 | sed -u 's/^/[build] /'; then
            echo "__ROSATLAS_ERROR__ colcon build failed."
            exit 1
        fi
        """;
    }

    private static string CanDownStep(LauncherConfig config) => config.CanDownOnExit
        ? """run_root ip link set "$CAN_CHANNEL" down 2>/dev/null || true"""
        : ":";
}

/// <summary>Shell quoting helpers.</summary>
public static class Shell
{
    /// <summary>Single-quotes a value for POSIX shells, escaping any embedded quote.</summary>
    public static string Quote(string value) => "'" + value.Replace("'", "'\\''") + "'";
}
