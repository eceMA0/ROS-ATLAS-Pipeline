"""EXAMPLE CAN decoder - illustrative values only, NOT a real device's CAN matrix.

This file ships publicly to show how a CAN board is decoded and turned into ROS
messages that the Foxglove bridge then forwards to ATLAS. The arbitration IDs, bit
offsets and scaling factors below are made-up placeholders for documentation only;
they will not decode any real hardware.

If a private mapping module (`_decoder_private.py`, git-ignored) is present, its real
definitions are used instead - that is how the maintainer runs the actual hardware.
Public users get the EXAMPLE decoder and can study the flow or drive it with fake
frames; to run real hardware you supply your own `_decoder_private.py`.

Frames (example layout, big-endian sequential bit numbering - bit 0 = MSB of byte 0):
  0x100 Data1: mux id + digitals + two axis channels + rotary + a 16-bit analogue
  0x101 Data2: analogue mux (3 channels/frame) + slow-row mux (temp, rail, ids)
"""

try:
    # Real, confidential CAN map (git-ignored). Present only on the maintainer's machine.
    from caninput_can._decoder_private import (  # type: ignore  # noqa: F401
        DATA1,
        DATA2,
        NUM_ANALOG,
        NUM_DIGITAL,
        NUM_ROTARY,
        CanInputState,
        bits,
    )
    USING_EXAMPLE_MAP = False
except ImportError:
    USING_EXAMPLE_MAP = True

    # ----- EXAMPLE (fake) CAN map -----
    DATA1 = 0x100
    DATA2 = 0x101

    NUM_DIGITAL = 8
    NUM_ANALOG = 5
    NUM_ROTARY = 5

    # Round, obviously-illustrative scaling factors (not the real ones).
    AUX_VOLTS_PER_BIT = 0.0001
    AN_VOLTS_PER_BIT = 0.005
    SUPPLY_VOLTS_PER_BIT = 0.02

    def bits(data: bytes, start: int, size: int) -> int:
        """Big-endian sequential extract: bit 0 is the MSB of byte 0."""
        raw = int.from_bytes(data, "big")
        total = len(data) * 8
        return (raw >> (total - start - size)) & ((1 << size) - 1)

    class CanInputState:
        """Latest decoded value of every example signal, plus when each group last updated.

        Same public shape as the real decoder so the ROS node, conversion layer and
        Foxglove bridge behave identically - only the numbers are fake.
        """

        def __init__(self):
            # Data1
            self.digital = [False] * NUM_DIGITAL
            self.axis_percent = [0.0, 0.0]
            self.axis_error = [False, False]
            self.rotary_position = [0] * NUM_ROTARY
            self.aux_raw = 0
            self.inputs_stamp = None
            # Data2 analogue mux
            self.analog_volts = [0.0] * NUM_ANALOG
            self.analog_min_error = [False] * NUM_ANALOG
            self.analog_max_error = [False] * NUM_ANALOG
            self.supply_volts = 0.0
            self.analog_stamp = None
            # Data2 slow row
            self.board_temp_c = 0.0
            self.rail_volts = 0.0
            self.aux_alt_mode = False
            self.led_state = [False, False, False]
            self.device_id = 0
            self.profile_id = 0
            self.status_stamp = None

        @property
        def aux_volts(self) -> float:
            offset = -2.5 if self.aux_alt_mode else 0.0
            return self.aux_raw * AUX_VOLTS_PER_BIT + offset

        def feed(self, arbitration_id: int, data: bytes, timestamp: float) -> bool:
            """Decode one example CAN frame. Returns True if it was a data frame."""
            if len(data) != 8:
                return False
            if arbitration_id == DATA1:
                self._on_data1(bytes(data))
                self.inputs_stamp = timestamp
            elif arbitration_id == DATA2:
                self._on_data2(bytes(data), timestamp)
            else:
                return False
            return True

        def _on_data1(self, d: bytes) -> None:
            mux = bits(d, 0, 4)
            dig = bits(d, 4, NUM_DIGITAL)
            self.digital = [bool((dig >> i) & 1) for i in range(NUM_DIGITAL)]
            self.axis_error = [bool(bits(d, 22, 1)), bool(bits(d, 23, 1))]
            self.axis_percent = [bits(d, 24, 10) * 0.1, bits(d, 34, 10) * 0.1]
            if mux < NUM_ROTARY:
                self.rotary_position[mux] = bits(d, 44, 4) + 1
            self.aux_raw = bits(d, 48, 16)

        def _on_data2(self, d: bytes, timestamp: float) -> None:
            anid = bits(d, 0, 1)
            for slot in range(3):
                base = 2 + slot * 12
                raw = bits(d, base, 10)
                if anid == 1 and slot == 2:
                    self.supply_volts = raw * SUPPLY_VOLTS_PER_BIT
                    continue
                ch = anid * 3 + slot
                self.analog_volts[ch] = raw * AN_VOLTS_PER_BIT
                self.analog_min_error[ch] = bool(bits(d, base + 10, 1))
                self.analog_max_error[ch] = bool(bits(d, base + 11, 1))
            self.analog_stamp = timestamp
            if self._on_slow_row(bits(d, 40, 8), bits(d, 48, 16)):
                self.status_stamp = timestamp

        def _on_slow_row(self, index: int, word: int) -> bool:
            """Apply one example slow-row value. Returns False for untracked rows."""
            if index == 30:
                self.board_temp_c = (word & 0x3FFF) * 0.03125 - 40.0
            elif index == 31:
                self.rail_volts = (word & 0x3FF) * 0.005
            elif index == 32:
                self.aux_alt_mode = bool(word & 1)
            elif 33 <= index <= 35:
                self.led_state[index - 33] = bool(word & 1)
            elif index == 36:
                self.device_id = word
            elif index == 37:
                self.profile_id = word & 0xFF
            else:
                return False
            return True
