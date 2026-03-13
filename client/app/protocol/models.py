"""Data models for P2L state and operations."""
from dataclasses import dataclass, field
from datetime import datetime
from typing import Optional

from .enums import LightColor, LightState, ModuleStatus, CompletionStatus


@dataclass
class LightModuleState:
    address: str
    status: ModuleStatus = ModuleStatus.STANDBY
    display_data: str = "00000"
    color: LightColor = LightColor.OFF
    led_mode: LightState = LightState.OFF
    last_instruction_at: Optional[datetime] = None
    last_confirmed_at: Optional[datetime] = None
    adjusted_quantity: Optional[str] = None
    block_no: str = "00"
    label: str = ""

    def to_dict(self) -> dict:
        return {
            "address": self.address,
            "status": self.status.name,
            "status_code": self.status.value,
            "display_data": self.display_data,
            "color": self.color.name,
            "color_code": self.color.value,
            "led_mode": self.led_mode.name,
            "last_instruction_at": self.last_instruction_at.isoformat() if self.last_instruction_at else None,
            "last_confirmed_at": self.last_confirmed_at.isoformat() if self.last_confirmed_at else None,
            "adjusted_quantity": self.adjusted_quantity,
            "block_no": self.block_no,
            "label": self.label,
        }


@dataclass
class PickingInstruction:
    address: str
    quantity: int
    color: LightColor = LightColor.GREEN
    led_mode: LightState = LightState.ON
    block_no: str = "00"
    label: str = ""

    @property
    def formatted_display(self) -> str:
        return f"{self.quantity:>5}"[:5]

    @property
    def normalized_address(self) -> str:
        return self.address.zfill(4)[-4:]


@dataclass
class ConfirmationEvent:
    address: str
    status: CompletionStatus
    adjusted_display: Optional[str]
    received_at: datetime
    seq_no: int

    def to_dict(self) -> dict:
        return {
            "address": self.address,
            "status": self.status.name,
            "adjusted_display": self.adjusted_display,
            "received_at": self.received_at.isoformat(),
        }
