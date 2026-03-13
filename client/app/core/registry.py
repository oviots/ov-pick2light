"""Thread-safe in-memory registry of all known light module states."""
import threading
from datetime import datetime, timezone
from typing import Callable, Optional

from ..protocol.enums import LightColor, LightState, ModuleStatus
from ..protocol.models import ConfirmationEvent, LightModuleState, PickingInstruction


StateChangeCallback = Callable[[list[LightModuleState]], None]


class LightModuleRegistry:
    """Single source of truth for all light module states."""

    def __init__(self) -> None:
        self._modules: dict[str, LightModuleState] = {}
        self._lock = threading.Lock()
        self._on_state_change: Optional[StateChangeCallback] = None

    def set_state_change_callback(self, cb: StateChangeCallback) -> None:
        self._on_state_change = cb

    @staticmethod
    def _normalize(address: str) -> str:
        return address.zfill(4)[-4:]

    # ─── Mutations ────────────────────────────────────────────────────────────

    def set_instructed(self, instruction: PickingInstruction) -> None:
        addr = self._normalize(instruction.address)
        with self._lock:
            module = self._modules.get(addr) or LightModuleState(address=addr)
            module.status = ModuleStatus.OPERATING
            module.display_data = instruction.formatted_display
            module.color = instruction.color
            module.led_mode = instruction.led_mode
            module.last_instruction_at = datetime.now(timezone.utc)
            module.last_confirmed_at = None
            module.adjusted_quantity = None
            module.block_no = instruction.block_no
            module.label = instruction.label
            self._modules[addr] = module
        self._notify()

    def set_confirmed(self, event: ConfirmationEvent) -> None:
        from ..protocol.enums import CompletionStatus
        addr = self._normalize(event.address)
        with self._lock:
            module = self._modules.get(addr)
            if module is None:
                module = LightModuleState(address=addr)
                self._modules[addr] = module
            if event.status == CompletionStatus.SHORTAGE:
                module.status = ModuleStatus.SHORTAGE
            else:
                module.status = ModuleStatus.COMPLETED
            module.last_confirmed_at = event.received_at
            module.adjusted_quantity = event.adjusted_display
        self._notify()

    def set_terminated(self, address: str) -> None:
        addr = self._normalize(address)
        with self._lock:
            module = self._modules.get(addr)
            if module:
                module.status = ModuleStatus.STANDBY
                module.color = LightColor.OFF
                module.led_mode = LightState.OFF
                module.display_data = "00000"
        self._notify()

    def set_scanned(self, address: str, status: ModuleStatus) -> None:
        addr = self._normalize(address)
        with self._lock:
            module = self._modules.get(addr) or LightModuleState(address=addr)
            module.status = status
            self._modules[addr] = module
        self._notify()

    def add_device(self, address: str, label: str = "", order_no: int = 0) -> LightModuleState:
        """Register a new device in standby state."""
        addr = self._normalize(address)
        with self._lock:
            if addr not in self._modules:
                self._modules[addr] = LightModuleState(address=addr, label=label, order_no=order_no)
        self._notify()
        return self._modules[addr]

    def remove_device(self, address: str) -> None:
        addr = self._normalize(address)
        with self._lock:
            self._modules.pop(addr, None)
        self._notify()

    def clear(self) -> None:
        with self._lock:
            self._modules.clear()
        self._notify()

    # ─── Queries ──────────────────────────────────────────────────────────────

    @property
    def all(self) -> list[LightModuleState]:
        with self._lock:
            return sorted(self._modules.values(), key=lambda m: (m.order_no, m.address))

    def get(self, address: str) -> Optional[LightModuleState]:
        return self._modules.get(self._normalize(address))

    @property
    def active_count(self) -> int:
        with self._lock:
            return sum(1 for m in self._modules.values()
                       if m.status in (ModuleStatus.OPERATING, ModuleStatus.OPERATING2))

    @property
    def pending_confirm_count(self) -> int:
        with self._lock:
            return sum(1 for m in self._modules.values()
                       if m.status == ModuleStatus.COMPLETED)

    @property
    def total_count(self) -> int:
        return len(self._modules)

    # ─── Notify ───────────────────────────────────────────────────────────────

    def _notify(self) -> None:
        if self._on_state_change:
            try:
                self._on_state_change(self.all)
            except Exception:
                pass
