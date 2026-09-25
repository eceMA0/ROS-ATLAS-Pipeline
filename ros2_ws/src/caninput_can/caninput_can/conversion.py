"""Snapshot a CanInputState into caninput_msgs messages.

Each to_*_msg() returns None until the board has sent a frame for that group, so callers
never publish a message built purely from defaults. Arrays are copied, so a message
is unaffected by frames decoded after it was built.
"""

from builtin_interfaces.msg import Time
from caninput_msgs.msg import CanInputAnalog, CanInputInputs, CanInputStatus

from caninput_can.decoder import CanInputState

DEFAULT_FRAME_ID = 'caninput'


def stamp_from_seconds(t: float) -> Time:
    """Convert a receive time in float seconds (e.g. python-can's msg.timestamp) to a ROS Time."""
    sec = int(t)
    nanosec = round((t - sec) * 1e9)
    if nanosec >= 1_000_000_000:
        sec, nanosec = sec + 1, 0
    return Time(sec=sec, nanosec=nanosec)


def _set_header(msg, stamp: float, frame_id: str) -> None:
    msg.header.stamp = stamp_from_seconds(stamp)
    msg.header.frame_id = frame_id


def to_inputs_msg(state: CanInputState, frame_id: str = DEFAULT_FRAME_ID) -> CanInputInputs | None:
    if state.inputs_stamp is None:
        return None
    msg = CanInputInputs()
    _set_header(msg, state.inputs_stamp, frame_id)
    msg.digital = list(state.digital)
    msg.axis_percent = list(state.axis_percent)
    msg.axis_error = list(state.axis_error)
    msg.rotary_position = list(state.rotary_position)
    msg.aux_volts = state.aux_volts
    return msg


def to_analog_msg(state: CanInputState, frame_id: str = DEFAULT_FRAME_ID) -> CanInputAnalog | None:
    if state.analog_stamp is None:
        return None
    msg = CanInputAnalog()
    _set_header(msg, state.analog_stamp, frame_id)
    msg.volts = list(state.analog_volts)
    msg.min_error = list(state.analog_min_error)
    msg.max_error = list(state.analog_max_error)
    msg.supply_volts = state.supply_volts
    return msg


def to_status_msg(state: CanInputState, frame_id: str = DEFAULT_FRAME_ID) -> CanInputStatus | None:
    if state.status_stamp is None:
        return None
    msg = CanInputStatus()
    _set_header(msg, state.status_stamp, frame_id)
    msg.board_temp_c = state.board_temp_c
    msg.rail_volts = state.rail_volts
    msg.aux_alt_mode = state.aux_alt_mode
    msg.led_state = list(state.led_state)
    msg.device_id = state.device_id
    msg.profile_id = state.profile_id
    return msg
