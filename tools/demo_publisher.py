#!/usr/bin/env python3
"""Publish fake CAN-input, IMU and text topics, for exercising the ATLAS bridge without hardware.

    source /opt/ros/jazzy/setup.bash && source <caninput workspace>/install/setup.bash
    python3 demo_publisher.py
"""

import math

import rclpy
from rclpy.node import Node
from sensor_msgs.msg import Imu
from caninput_msgs.msg import CanInputInputs, CanInputStatus
from std_msgs.msg import String


class DemoPublisher(Node):

    def __init__(self):
        super().__init__('demo_publisher')
        self.inputs_pub = self.create_publisher(CanInputInputs, 'caninput/inputs', 10)
        self.status_pub = self.create_publisher(CanInputStatus, 'caninput/status', 10)
        self.imu_pub = self.create_publisher(Imu, 'imu', 10)
        self.text_pub = self.create_publisher(String, 'chatter', 10)  # no numbers: the bridge should ignore it
        self.start = self.get_clock().now()
        self.create_timer(0.02, self.publish_fast)  # 50 Hz
        self.create_timer(1.0, self.publish_slow)

    def publish_fast(self):
        now = self.get_clock().now()
        t = (now - self.start).nanoseconds * 1e-9

        inputs = CanInputInputs()
        inputs.header.stamp = now.to_msg()
        inputs.header.frame_id = 'caninput'
        inputs.digital = [int(t) % 8 == i for i in range(8)]
        inputs.axis_percent = [50 + 50 * math.sin(t), 50 + 50 * math.cos(t)]
        inputs.rotary_position = [1 + (int(t) + i) % 16 for i in range(5)]
        inputs.aux_volts = 2.5 + 2.0 * math.sin(2 * t)
        self.inputs_pub.publish(inputs)

        imu = Imu()
        imu.header.stamp = now.to_msg()
        imu.header.frame_id = 'imu'
        imu.orientation.w = 1.0
        imu.angular_velocity.z = 0.5 * math.sin(t)
        imu.linear_acceleration.z = 9.81 + 0.1 * math.sin(10 * t)
        self.imu_pub.publish(imu)

    def publish_slow(self):
        status = CanInputStatus()
        status.header.stamp = self.get_clock().now().to_msg()
        status.board_temp_c = 30.0
        status.rail_volts = 5.0
        status.device_id = 1501
        status.profile_id = 7
        self.status_pub.publish(status)
        self.text_pub.publish(String(data='hello from the demo publisher'))


def main():
    rclpy.init()
    node = DemoPublisher()
    try:
        rclpy.spin(node)
    except KeyboardInterrupt:
        pass
    finally:
        node.destroy_node()
        rclpy.try_shutdown()


if __name__ == '__main__':
    main()
