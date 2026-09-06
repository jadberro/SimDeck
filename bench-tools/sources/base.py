"""Data source interface.

Anything that can supply named values to the hub implements this. The hub
never knows whether it is talking to FSUIPC, a mock, or X-Plane later on.

Names passed in and out are RAW source names (e.g. an actual LVAR string),
not logical names. Translation happens in the hub via the aircraft profile.
"""

from abc import ABC, abstractmethod
from typing import Dict, Iterable, Optional


class DataSource(ABC):

    @abstractmethod
    def start(self) -> None:
        ...

    def stop(self) -> None:
        pass

    @property
    @abstractmethod
    def connected(self) -> bool:
        """True if the source is currently receiving from the sim."""

    @property
    def aircraft(self) -> Optional[str]:
        """Aircraft title/identifier, used for profile matching. May be None."""
        return None

    @abstractmethod
    def set_watchlist(self, names: Iterable[str]) -> None:
        """Tell the source which raw names the hub currently cares about.

        Called whenever the set of subscribed modules changes. Sources that
        must poll (LVARs) use this to build their poll list; sources that get
        everything for free can ignore it.
        """

    @abstractmethod
    def read(self) -> Dict[str, float]:
        """Latest snapshot of the watchlist. Missing names are simply absent."""

    def write(self, name: str, value: float) -> None:
        """Optional: push a value back to the sim (for module inputs)."""
        raise NotImplementedError
