# Analogue inputs: snapshot of the latest decoded analogue frame.
# Channels are multiplexed, so each holds the last value seen.

std_msgs/Header header      # stamp = receive time of the latest analogue frame

float32[] volts             # analogue channels [V] (index 0 = channel 1)
bool[] min_error            # channel below its configured minimum
bool[] max_error            # channel above its configured maximum
float32 supply_volts        # supply voltage diagnostic [V]
