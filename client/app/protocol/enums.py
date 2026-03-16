"""Aioi protocol enumerations."""
from enum import IntEnum


class LightState(IntEnum):
    OFF = 0x1
    ON = 0x2
    FLASH = 0x3
    HIGH_FLASH = 0x4
    NO_CHANGE = 0xF


class LightColor(IntEnum):
    OFF = 0
    RED = 1
    GREEN = 2
    BLUE = 3
    YELLOW = 4   # LED2 (Red) + LED1 (Green)
    CYAN = 5     # LED1 (Green) + LED0 (Blue)
    MAGENTA = 6  # LED2 (Red) + LED0 (Blue)
    WHITE = 7    # LED0 + LED1 + LED2


class ModuleStatus(IntEnum):
    """ASCII-encoded values: enum value == ASCII code of status byte."""
    POWER_ON    = 0x30  # '0'
    STANDBY     = 0x31  # '1'
    OPERATING   = 0x32  # '2'
    OPERATING2  = 0x33  # '3'
    COMPLETED   = 0x34  # '4'
    SHORTAGE    = 0x35  # '5'
    MAINTENANCE = 0x36  # '6'
    MALFUNCTION = 0x3F  # '?'


class CompletionStatus(IntEnum):
    NORMAL   = 0
    ABNORMAL = 1
    SHORTAGE = 10


def color_to_led_states(color: LightColor, active_mode: LightState = LightState.ON):
    """Returns (LED2/Red, LED1/Green, LED0/Blue) LightState tuple for the given color.

    Physical LED assignment per Aioi spec §5.5.2 (full-color modules AW2xxxF, TW2xxxF, etc.):
        LED0 → Blue, LED1 → Green, LED2 → Red
    """
    off = LightState.OFF
    m = active_mode
    return {
        LightColor.OFF:     (off, off, off),
        LightColor.RED:     (m,   off, off),   # LED2 (Red) on
        LightColor.GREEN:   (off, m,   off),   # LED1 (Green) on
        LightColor.BLUE:    (off, off, m),     # LED0 (Blue) on
        LightColor.YELLOW:  (m,   m,   off),   # LED2 (Red) + LED1 (Green)
        LightColor.CYAN:    (off, m,   m),     # LED1 (Green) + LED0 (Blue)
        LightColor.MAGENTA: (m,   off, m),     # LED2 (Red) + LED0 (Blue)
        LightColor.WHITE:   (m,   m,   m),     # All LEDs on
    }.get(color, (off, off, off))
