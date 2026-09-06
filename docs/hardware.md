# Hardware

## Chosen panel board: Waveshare ESP32-S3-Touch-LCD-1.85

- ESP32-S3R8, 16 MB flash, 8 MB PSRAM, Wi-Fi
- 1.85" round IPS, **360 × 360**, ST77916 over QSPI; CST816 touch on the
  touch variant (a cheaper non-touch variant exists and is sufficient)
- Board is **55 × 55 mm** - a 2ATI panel cutout is 55.9 mm
- Active area ~47 mm; the measured dial face is 45–50 mm. Near true size.

### Power - no button required

The PWR button is the *battery* latch only. On USB-C power there is no latch
in the circuit; the board boots and runs its firmware the moment 5 V arrives
and stays on while power is present. Do not fit a battery. BOOT only matters
if held during power-up.

### Firmware-relevant wiring

| Signal | Pin | Note |
|---|---|---|
| LCD data | GPIO 46/45/42/41 | QSPI SDA0–3 |
| LCD clock / CS / TE | GPIO 40 / 21 / 18 | |
| **LCD reset** | **EXIO2** | via **TCA9554** I/O expander (I2C 0x20) - must be released in firmware |
| Backlight | GPIO 5 | PWM-capable; dimmable from a sim lighting knob later |
| Touch I2C | GPIO 1 (SDA) / 3 (SCL), INT 4, RST EXIO1 | |
| Shared I2C | GPIO 10 (SCL) / 11 (SDA) | IMU, RTC, expander |

Onboard I2C addresses in use: 0x15, 0x20, 0x51.

### Partition scheme

Must have two app slots or OTA fails at `Update.begin()`. In Arduino, "16M
Flash (3MB APP/9.9MB FATFS)" has two. Avoid any "Huge APP" scheme.

### First thing to do when it arrives

Flash Waveshare's own test firmware (in their demo download) and confirm the
screen lights and the demo runs. Separates "board problem" from "our firmware
problem" before either exists.

## Also evaluated

**BIGTREETECH Panda Touch** (ESP32-S3, 5" 800×480 RGB, 16 MB/8 MB). A
community BSP exists (`fmauNeko/pandatouch-bsp`, MIT). Its own LVGL benchmark
shows rotated ARGB images at 11 fps / 99 % CPU - the parallel RGB panel
saturates memory bandwidth - so pointers there must be drawn as filled polygons
and shadows dropped. Flashing custom firmware erases the stock software; back
it up first with `esptool.py read_flash 0 0x1000000 backup.bin`. Parked.

## Real instrument dimensions

ARINC 408A square case sizes, from an Avionics Mounts clamp drawing:

| Size | Clamp A | Clamp B | Clamp C | Panel cutout |
|---|---|---|---|---|
| 2ATI-S | 55.2 mm | 52.8 mm | 63.5 mm | ~55.9 mm |
| 3ATI-S | 80.6 mm | 77.7 mm | 98.7 mm | ~81.3 mm |

Which size the A320 triple indicator actually is was **not confirmed** from
any datasheet. 2ATI is the working assumption and the chosen board matches it.
