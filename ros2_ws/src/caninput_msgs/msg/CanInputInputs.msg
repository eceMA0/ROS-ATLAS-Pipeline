# Digital and axis inputs: snapshot of the latest decoded input frame.
# Multiplexed fields hold the last value seen.

std_msgs/Header header      # stamp = receive time of the latest input frame

bool[] digital              # digital inputs (index 0 = channel 1); true = active
float32[] axis_percent      # proportional axis channels [%]
bool[] axis_error           # axis channels in error state
uint8[] rotary_position     # rotary switch positions (index 0 = channel 1); 0 = not received yet
float32 aux_volts           # auxiliary analogue input [V]
