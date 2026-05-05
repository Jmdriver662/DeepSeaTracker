# DeepSeaTracker
**Version:** 2.1.0  
**Framework:** Oxide/uMod  
**Game:** Rust

---

## Overview

DeepSeaTracker tracks the Deep Sea monument's open/close cycle in real time using Rust's native `DeepSeaManager` and displays a compact HUD status bar on every player's screen. It reads live data directly from the game engine — no manual timers or guesswork.

The bar shows the current state, a countdown timer, and a compass arrow indicating which side of the map the active portal has spawned on.

---

## HUD States

| State | Color | Description |
|---|---|---|
| **OPEN** | Green | Monument is accessible. Counts down to close. |
| **RADS** | Orange | Monument is irradiated and about to wipe. Counts down to wipe. |
| **CLOSED** | Red | Monument is closed. Counts down to next opening. |
| **BUSY** | Grey | Transitioning between states. |
| **N/A** | Grey | Deep Sea monument not present on this map. |

---

## Compass Arrow

The HUD displays a directional arrow in the upper right corner of the bar indicating which edge of the map the active Deep Sea portal has spawned on:

| Arrow | Direction |
|---|---|
| `▲N` | North |
| `▼S` | South |
| `►E` | East |
| `◄W` | West |

The arrow is determined by scanning for the active portal entity (the one with the `Open` flag) and persists correctly across server restarts. It recalculates automatically each time the monument opens on a new cycle. Shows `?` if the portal has not yet been detected.

---

## Installation

1. Drop `DeepSeaTracker.cs` into your `oxide/plugins/` folder.
2. Oxide will compile and load the plugin automatically.
3. The config file will be created at `oxide/config/DeepSeaTracker.json` on first load.

---

## Commands

### Chat Commands

| Command | Description |
|---|---|
| `/deepsea` | Prints the current Deep Sea status and countdown timer to the player in chat (private, only visible to them). |
| `/dsbar` | Toggles the HUD status bar on or off for the player who types it. Preference resets on disconnect. |

### Console Commands

| Command | Description |
|---|---|
| `deepsea.status` | Prints the current status and countdown to the server console. |

---

## Configuration

Config file location: `oxide/config/DeepSeaTracker.json`

After editing the config run `o.reload DeepSeaTracker` in the server console to apply changes without a restart.

```json
{
  "BarColor": "0.1 0.4 0.6 0.85",
  "OpenColor": "0.2 0.7 0.4 1.0",
  "RadColor": "0.85 0.6 0.1 1.0",
  "ClosedColor": "0.7 0.2 0.2 1.0",
  "BusyColor": "0.6 0.6 0.6 1.0",
  "TimerColor": "1.0 1.0 1.0 1.0",
  "Transparency": 0.85,
  "AnchorMin": "0.894 0.888",
  "AnchorMax": "0.99 0.926",
  "FontSize": 10,
  "UpdateInterval": 1.0
}
```

### Config Reference

| Field | Description | Default |
|---|---|---|
| `BarColor` | Background color of the bar (R G B A) | `0.1 0.4 0.6 0.85` |
| `OpenColor` | Color when monument is open (R G B A) | `0.2 0.7 0.4 1.0` |
| `RadColor` | Color when monument is irradiated (R G B A) | `0.85 0.6 0.1 1.0` |
| `ClosedColor` | Color when monument is closed (R G B A) | `0.7 0.2 0.2 1.0` |
| `BusyColor` | Color when transitioning (R G B A) | `0.6 0.6 0.6 1.0` |
| `TimerColor` | Color of the countdown digits (R G B A) | `1.0 1.0 1.0 1.0` |
| `Transparency` | Background opacity — `0.0` invisible, `1.0` fully opaque | `0.85` |
| `AnchorMin` | Bottom-left corner of the bar (X Y, range 0.0–1.0) | `0.894 0.888` |
| `AnchorMax` | Top-right corner of the bar (X Y, range 0.0–1.0) | `0.99 0.926` |
| `FontSize` | Text size for all HUD labels — minimum 10 recommended | `10` |
| `UpdateInterval` | How often in seconds the countdown timer redraws (clamped 0.5–60) | `1.0` |

### Positioning Guide

Colors use the format `R G B A` where each value is between `0.0` and `1.0`.

Anchors use Rust's screen coordinate system:
- X: `0.0` = left edge, `1.0` = right edge
- Y: `0.0` = bottom edge, `1.0` = top edge

To move the bar, adjust both `AnchorMin` and `AnchorMax` by the same amount. To resize, change the gap between them. The default position is the upper right corner.

### Performance Tuning

On servers with high player counts, increase `UpdateInterval` to reduce CUI network traffic. Since the timer displays hours and minutes most of the time, players will barely notice the difference:

| Player Count | Recommended UpdateInterval |
|---|---|
| < 100 | `1.0` |
| 100–200 | `5.0` |
| 200+ | `10.0` |

---

## Technical Notes

- Data is read directly from `DeepSeaManager.ServerInstance` on every tick — no local countdown, no drift.
- The static panel (background, labels, arrow) is only rebuilt when the state changes, not every tick.
- The timer panel is the only element redrawn each tick, and only if the formatted string has changed.
- The bar background color is cached on load to avoid string operations each tick.
- The compass arrow is determined by finding the `deepsea_portal` entity with the `Open` flag and reading its world position.
- `UpdateInterval` is clamped between `0.5` and `60` seconds to prevent misconfiguration issues.

---

## Changelog

### 2.1.0
- Added compass arrow showing which map edge the active portal is on
- Added `_hiddenPlayers` set so `/dsbar` hide persists correctly across ticks
- Arrow calculated on server init so it survives restarts
- Split static/timer panels for improved performance
- Added `_lastTimerText` cache to skip redundant timer redraws
- Added `_cachedBarColor` to eliminate per-tick string allocations
- Added `UpdateInterval` config with clamp safety

### 1.0.0
- Initial release
