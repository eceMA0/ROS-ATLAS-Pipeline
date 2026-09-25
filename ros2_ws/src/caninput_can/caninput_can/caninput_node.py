"""ROS 2 node: read CAN input-board frames, decode them, and publish caninput_msgs snapshots.

    ros2 run caninput_can caninput_node
    ros2 run caninput_can caninput_node --ros-args -p channel:=can0 -p publish_rate_hz:=200.0

CAN is read on python-can's Notifier thread, which feeds a CanInputState; timers on the ROS
executor snapshot that state. A group is only republished when a new frame has updated it,
so a stalled bus produces no duplicate samples downstream.
"""

import sys
import threading

import can
import rclpy
from rclpy.executors import ExternalShutdownException
from rclpy.logging import get_logger
from rclpy.node import Node
from caninput_msgs.msg import CanInputAnalog, CanInputInputs, CanInputStatus

from caninput_can.conversion import to_analog_msg, to_inputs_msg, to_status_msg
from caninput_can.decoder import CanInputState

QOS_DEPTH = 10
WATCHDOG_PERIOD_S = 2.0


class CanInputNode(Node):

    def __init__(self, **kwargs):
        super().__init__('caninput_node', **kwargs)
        interface = self.declare_parameter('interface', 'socketcan').value
        self._channel = self.declare_parameter('channel', 'can0').value
        self._frame_id = self.declare_parameter('frame_id', 'caninput').value
        publish_rate_hz = self.declare_parameter('publish_rate_hz', 100.0).value
        status_rate_hz = self.declare_parameter('status_rate_hz', 1.0).value
        if publish_rate_hz <= 0 or status_rate_hz <= 0:
            raise ValueError('publish_rate_hz and status_rate_hz must be > 0')

        self._state = CanInputState()
        self._lock = threading.Lock()  # guards _state and _frames (Notifier thread vs executor)
        self._frames = 0               # board frames decoded since the last watchdog check
        self._last_stamp = {}          # group -> stamp of its last published snapshot
        self._receiving = None         # unknown until the first watchdog check

        self._inputs_pub = self.create_publisher(CanInputInputs, 'caninput/inputs', QOS_DEPTH)
        self._analog_pub = self.create_publisher(CanInputAnalog, 'caninput/analog', QOS_DEPTH)
        self._status_pub = self.create_publisher(CanInputStatus, 'caninput/status', QOS_DEPTH)

        try:
            self._bus = can.Bus(interface=interface, channel=self._channel)
        except Exception as ex:  # the exception type depends on the python-can interface
            raise RuntimeError(
                f"Could not open {interface} channel '{self._channel}': {ex}\n"
                f'Is it up? e.g. sudo ip link set {self._channel} up type can bitrate 1000000'
            ) from ex
        self._notifier = can.Notifier(self._bus, [self._on_frame])

        self.create_timer(1.0 / publish_rate_hz, self._publish_fast)
        self.create_timer(1.0 / status_rate_hz, self._publish_status)
        self.create_timer(WATCHDOG_PERIOD_S, self._watchdog)
        self.get_logger().info(
            f"Reading CAN input board on {interface} '{self._channel}', publishing at "
            f'{publish_rate_hz:g} Hz (status {status_rate_hz:g} Hz)')

    def _on_frame(self, frame: can.Message) -> None:
        # Runs on the Notifier thread. Data frames use 11-bit IDs.
        if frame.is_extended_id or frame.is_remote_frame or frame.is_error_frame:
            return
        with self._lock:
            if self._state.feed(frame.arbitration_id, frame.data, frame.timestamp):
                self._frames += 1

    def _publish_fast(self) -> None:
        self._publish_if_new('inputs', to_inputs_msg, self._inputs_pub)
        self._publish_if_new('analog', to_analog_msg, self._analog_pub)

    def _publish_status(self) -> None:
        self._publish_if_new('status', to_status_msg, self._status_pub)

    def _publish_if_new(self, group: str, to_msg, publisher) -> None:
        with self._lock:
            stamp = getattr(self._state, f'{group}_stamp')
            if stamp is None or stamp == self._last_stamp.get(group):
                return  # nothing received yet, or no new frames since the last publish
            msg = to_msg(self._state, self._frame_id)
        self._last_stamp[group] = stamp
        publisher.publish(msg)

    def _watchdog(self) -> None:
        with self._lock:
            frames, self._frames = self._frames, 0
        # Log transitions only, so a dead bus doesn't spam the console.
        if frames and self._receiving is not True:
            self.get_logger().info(
                f'Receiving CAN frames ({frames / WATCHDOG_PERIOD_S:.0f}/s)')
        elif not frames and self._receiving is not False:
            self.get_logger().warn(
                f"No CAN frames on '{self._channel}' for {WATCHDOG_PERIOD_S:g} s - "
                'is the board powered and the bus up?')
        self._receiving = bool(frames)

    def destroy_node(self) -> None:
        self._notifier.stop()
        self._bus.shutdown()
        super().destroy_node()


def main(args=None) -> int:
    rclpy.init(args=args)
    try:
        node = CanInputNode()
    except (RuntimeError, ValueError) as ex:
        get_logger('caninput_node').fatal(str(ex))
        rclpy.try_shutdown()
        return 1
    try:
        rclpy.spin(node)
    except (KeyboardInterrupt, ExternalShutdownException):
        pass
    finally:
        node.destroy_node()
        rclpy.try_shutdown()
    return 0


if __name__ == '__main__':
    sys.exit(main())
