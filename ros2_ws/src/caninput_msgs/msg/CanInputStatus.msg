# Unit health: values from the status multiplexor (one value per frame).
# Fields hold the last value seen; 0/false until the corresponding value is received.

std_msgs/Header header      # stamp = receive time of the latest tracked status value

float32 board_temp_c        # board temperature [degC]
float32 rail_volts          # internal regulated rail [V]
bool aux_alt_mode           # auxiliary input select: true = alternate mode, false = voltage
bool[] led_state            # status LEDs on
uint16 device_id            # attached device identifier
uint8 profile_id            # active configuration profile
