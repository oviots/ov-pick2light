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
    correlation_id: Optional[str] = None
    command_item_id: Optional[str] = None
    picking_detail_id: Optional[str] = None
    event_id: Optional[str] = None
    observed_status: Optional[ModuleStatus] = None
    confirmation_missing_at: Optional[datetime] = None
    confirmation_missing_event_id: Optional[str] = None
    can_cancel_test: bool = False
    label: str = ""
    order_no: int = 0

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
            "correlation_id": self.correlation_id,
            "command_item_id": self.command_item_id,
            "picking_detail_id": self.picking_detail_id,
            "event_id": self.event_id,
            "observed_status": self.observed_status.name if self.observed_status is not None else None,
            "confirmation_missing_at": self.confirmation_missing_at.isoformat() if self.confirmation_missing_at else None,
            "confirmation_missing_event_id": self.confirmation_missing_event_id,
            "can_cancel_test": self.can_cancel_test,
            "label": self.label,
            "order_no": self.order_no,
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
    correlation_id: Optional[str] = None
    command_item_id: Optional[str] = None
    picking_detail_id: Optional[str] = None
    event_id: Optional[str] = None
    label: Optional[str] = None
    display_data: Optional[str] = None
    source: Optional[str] = None

    def to_dict(self) -> dict:
        return {
            "address":          self.address,
            "status":           self.status.name,
            "adjusted_display": self.adjusted_display,
            "received_at":      self.received_at.isoformat(),
            "correlation_id":   self.correlation_id,
            "command_item_id": self.command_item_id,
            "picking_detail_id": self.picking_detail_id,
            "event_id": self.event_id,
            "label":            self.label,
            "display_data":     self.display_data,
            "source":           self.source,
        }
