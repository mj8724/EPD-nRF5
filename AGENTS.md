# Repository Guidelines

## Project Overview

EPD-nRF5 is a Nordic nRF5x BLE peripheral firmware that drives e-paper (e-ink) displays. Two usage modes:

- **Calendar / clock**: renders Chinese lunar calendar, solar terms, holidays, and clock on the panel.
- **Digital photo frame**: receives dithered images over Bluetooth and displays them.

A companion **web interface** (`html/`, Web Bluetooth API) performs image dithering and sends data to the device; it also switches the device between picture/calendar/clock modes and pushes OTA firmware updates.

Supported MCUs: `nrf51822` / `nrf51802` / `nrf52811` / `nrf52810`. Supported panels: UC81xx / SSD16xx series, black & white / 3-color / 4-color. The same firmware binary adapts to different screen sizes and drivers, switchable online through the web interface (config persisted on device).

License: **GPL-3.0**. Author: tsl0922.

## Architecture & Data Flow

### Two parallel firmware lines, one shared codebase

The GUI/EPD code is shared; only the BLE stack glue and SDK differ, selected by preprocessor:

| Line | MCU | SoftDevice | SDK | Project files |
|---|---|---|---|---|
| nRF52 | nRF52811_xxAA (Cortex-M4) | S112 7.3.0 | `SDK/17.1.0_ddde560` | `Keil/EPD-nRF52.uvprojx`, `Keil/DFU-nRF52.uvprojx` |
| nRF51 | nRF51822_xxAA (Cortex-M0) | S130 2.0.1 | `SDK/12.3.0_d7731ad` | `Keil/EPD-nRF51.uvprojx`, `Keil/DFU-nRF51.uvprojx` |

Each line has an app project (output `EPD-nRFxx.hex`), a DFU bootloader project (`bl_*.hex`), and a `flash_softdevice` target that downloads the prebuilt softdevice hex without compiling.

### Boot & init flow (`main.c`)

Standard Nordic peripheral app, in order: log init → save RESETREAS → WDT (`nrf_drv_wdt`) → timers → power management → BLE stack → scheduler → GAP → GATT → services init (`ble_epd_init` + DFU) → advertising → conn params → start 1 s clock timer → boot render → `for(;;) { app_sched_execute(); idle_state_handle(); }`. A 1 s `app_timer` increments a timestamp and calls `ble_epd_on_timer()`, which schedules GUI updates on the app scheduler. After a WDT reset, calendar mode is forced at boot.

### BLE protocol

Vendor GATT service — base UUID `EC5A671C-C1B6-46FB-8D91-28D822367562` (in code as byte-reversed `BLE_UUID_EPD_SVC_BASE`; the web app spells it reversed: `62750001-d828-918d-fb46-b6c11c675aec`):

- Service `0x0001`; write/notify characteristic `0x0002`; read-only app-version characteristic `0x0003` (current value `0x1a`).
- Opcode-prefixed command payloads, dispatched in `EPD_service.c` `epd_service_on_write()`: `0x00`–`0x06` (pin set, init, clear, raw cmd/data, refresh, sleep), `0x20` set time, `0x21` week start, `0x30` WRITE_IMAGE, `0x90` SET_CONFIG, `0x91` reset, `0x92` system sleep, `0x99` erase config + reset.
- WRITE_IMAGE `0x30` streams RLE-compressed pixel data chunked to MTU−2; per-chunk flags: BIT0 black plane, BIT1 first chunk, BIT2 RLE. RLE decompression happens in 255-byte stack chunks, fed straight to the panel RAM via `drv->write_ram` — **no full framebuffer on the MCU**, and the WDT is fed per chunk.

### Image/calendar render paths

1. **Raw image path**: BLE → RLE decompress (stack buffer) → `drv->write_ram` → panel RAM → refresh.
2. **GUI path** (`epd_gui_update()`): init GPIO/panel → build `gui_data_t` (temperature via `drv->read_temp`, voltage, SSID) → `DrawGUI(&data, buffer_callback, ctx)` renders calendar/clock into a **paged heap buffer** (Adafruit_GFX fork; page height derived from `__HEAP_SIZE`) → each page flushed via `drv->write_image` → full panel refresh (DRF) → sleep. **No partial-refresh waveform** — refreshes are full-panel.

### Config persistence

`epd_config_t` holds 13 `uint8_t` keys (pin mapping, model id, display mode, week start, …), persisted in **fds flash** (file `0x0000`, key `0x0001`; `0xFF` = unset). fds garbage collection is scheduled on `FDS_ERR_NO_SPACE_IN_FLASH`.

### Web frontend data flow

Static single-page app (`html/index.html`), no framework/build. Connects via `navigator.bluetooth.requestDevice` (accept-all + optional service UUID), reads the version characteristic to pick protocol, then pushes opcode-prefixed commands. Image data is dithered/quantized in-browser and chunked with an interleaved write-without-response ack; RLE compression is opt-in when firmware advertises `rle=1`. Firmware computes calendar content itself — the web side only sends time (SET_TIME) and mode.

## Key Directories

| Path | Purpose |
|---|---|
| `main.c`, `main.h` | App entry, init order, 1 s clock timer, sleep mode |
| `EPD/` | Panel + BLE layer: `EPD_service.c/h` (GATT service, opcode dispatch, RLE), `EPD_driver.c/h` (driver vtable, model table), `EPD_config.c/h` (fds config), `UC81xx.c`, `SSD16xx.c` (panel command sets) |
| `GUI/` | Platform-independent rendering: `GUI.c/h` (`DrawGUI`, calendar/clock), `Adafruit_GFX.c/h` (forked), `Lunar.c/h` (Chinese calendar), `fonts.c/h`, `u8g2_font.c/h` |
| `html/` | Web Bluetooth frontend: `js/main.js` (app + protocol), `js/dithering.js`, `js/rle.js`, `js/paint.js`, `js/crop.js`, `js/quotes.js`, `css/`, `v1.5/` (frozen legacy UI for firmware < 0x16) |
| `Keil/` | The only firmware build path — 4 uvprojx projects (app + bootloader × 2 MCU lines) |
| `SDK/` | Vendored Nordic SDKs: `17.1.0_ddde560` (nRF52), `12.3.0_d7731ad` (nRF51) |
| `tools/` | OTA packaging: `make-ota-nrf52.bat`, `make-ota-nrf51.bat`, `priv.pem` signing key |
| `docs/` | `develop.md` (build/flash), `devices.md` (supported hardware), `README.md` (OTP-derived LUT waveforms), `OTP/` (raw dumps), `datasheets/` |
| `emulator.c` | Win32 host emulator of a 400×300 e-paper display (built only by the Makefile) |

## Development Commands

### Host emulator (GUI preview, no hardware)

The root `Makefile` builds a **Windows MinGW GUI emulator** (`emulator.exe`) from `GUI/*.c` + `emulator.c` — it does NOT build firmware.

```bash
# MSYS2 MINGW64
pacman -S make mingw-w64-x86_64-gcc
make                 # → emulator.exe
make clean           # wipe objects
```

Emulator controls: Space = mode, R/W = color, arrow keys = week/date. This is the primary way to preview GUI/calendar changes on Windows without flashing.

### Firmware (Keil MDK)

Requires **Keil 5.36 or lower** (ARMCC 5.06u7). Programmer: J-Link or DAPLink; debug log via RTT (RTTView).

Flash order (per `docs/develop.md`):
1. Erase all.
2. Switch to the `flash_softdevice` target, download the softdevice — **without compiling**, once per device.
3. Switch to the app target (`nRF52811_xxAA` or `nRF51822_xxAA`), compile, download.

Optional external 32.768 kHz LF crystal: nRF51 → `NRF_CLOCK_LFCLKSRC` XTAL define in `main.c`; nRF52 → `NRF_SDH_CLOCK_LF_*` settings in `sdk_config.h`.

### OTA packaging

```bat
tools\make-ota-nrf52.bat   # nrfutil pkg generate (--key-file tools\priv.pem, app-version 0x19) + settings generate + mergehex → -ota.zip / -full.hex
tools\make-ota-nrf51.bat   # same pattern for nRF51 line
```

### Web frontend

No build step. Open `html/index.html` directly in a Web-Bluetooth-capable browser, or use the hosted GitHub Pages URL. CI (`static.yml`) deploys `html/` to Pages.

## Code Conventions & Common Patterns

- **Naming**: C uses snake_case with module prefixes — `epd_*` (panel), `ble_epd_*` (BLE service), `gui_*` (rendering), `drv->` (driver vtable methods). Web JS uses camelCase globals (`EpdCmd`, `paintManager`, `cropManager`, `quotesApp`) exposed via classic script tags (no modules).
- **Formatting**: `.clang-format` = Google base, 120-column limit, 4-space indent, no tabs. Match existing style in files you edit; do not reformat unrelated code.
- **GUI platform-independence contract** (explicit in `docs/develop.md`): GUI code must never touch MCU peripherals — fill a `gui_data_t` and call `DrawGUI(&data, buffer_callback, ctx)`. On device the callback is `drv->write_image`; in the emulator it's a `DrawBitmap` Win32 callback. Keep it portable.
- **Dual-SDK pattern**: guard SDK-version-specific BLE glue with `#if defined(S112)` — `nrf_sdh` style (SDK 17, nRF52) vs legacy `softdevice_handler` (SDK 12, nRF51). Changes must compile for both lines.
- **Driver abstraction**: `epd_driver_t` vtable (`init`/`write_ram`/`write_image`/`refresh`/`sleep`/`read_temp`); `epd_model_id_t` (0x01–0x11) maps model → driver + panel geometry. Add new panels as new entries, not new control flow.
- **Error handling**: Nordic `app_error_handler` + WDT feeding (including per-chunk during long RLE transfers). No defensive return-code checking on the GUI path — keep it that way.
- **Memory discipline**: no full framebuffer anywhere; heap page buffer sized `ph = (__HEAP_SIZE - 512) / (width / 8)`. Config persists via fds; `0xFF` bytes mean unset.
- **Web**: all state in globals (no localStorage), Chinese UI text, mixed Chinese/English comments, `?v=` cache-busting on script/css tags. Dithering/quantization lives in `dithering.js` (Lab-space palette match, 5 algorithms: Floyd-Steinberg, Atkinson, Stucki, Jarvis, Bayer 8×8).

## Important Files

| File | Why it matters |
|---|---|
| `main.c` | Entry point, init order, timer, sleep mode, WDT |
| `EPD/EPD_service.h` / `.c` | BLE GATT service, opcode dispatch, RLE decompress, GUI update scheduling, app-version char |
| `EPD/EPD_driver.h` | Driver vtable, model table, command enums (UC81xx/SSD16xx), pin primitives |
| `EPD/EPD_config.h` / `.c` | Config schema (13 keys) + fds persistence + GC |
| `GUI/GUI.h` / `.c` | `DrawGUI` calendar/clock rendering, festivals/holidays tables |
| `emulator.c` | Host emulator entry (`WinMain`), buffer callback |
| `html/js/main.js` | Web app core: connect, chunked write protocol, command table |
| `html/js/dithering.js` | Color quantization + dithering + bit packing (1bpp/2bpp/4bpp) |
| `Keil/*.uvprojx` | Sole firmware build definition (app + bootloader, both lines) |
| `Makefile` | Emulator build only — never assume it builds firmware |
| `tools/make-ota-*.bat` | DFU package generation pipeline |

## Runtime/Tooling Preferences

- **Firmware toolchain**: Keil MDK ≤ 5.36 with ARMCC 5.06u7. Do not migrate to newer AC6 without verifying both project lines.
- **Host emulator**: MinGW gcc via MSYS2, Windows-only, no dependencies beyond GDI (`-lgdi32 -mwindows`).
- **Web**: vanilla JS + Web Bluetooth API. No Node, npm, or build step. Browser must support Web Bluetooth (Chrome/Edge).
- **CI**: GitHub Actions — `build.yml` builds the emulator on `windows-latest` (MSYS2) and uploads it as an artifact; `static.yml` deploys `html/` to GitHub Pages. No firmware CI exists.
- **SDKs are vendored** under `SDK/`; `sdk_config.h` per line configures the Nordic stack.

## Testing & QA

- **No automated test suites** exist (firmware or web) — do not expect or invent one without being asked.
- **GUI/calendar changes**: verify via the Windows emulator (`make` → `emulator.exe`; Space/R/W/arrow keys). This is the established smoke-test path.
- **Protocol/BLE changes**: flash to real hardware, then exercise through `html/index.html` (send image, switch modes, set time). Both nRF51 and nRF52 lines should be checked when the shared code changes.
- **OTA**: package with `tools/make-ota-*.bat`, apply via the web UI's DFU flow.
- **CI coverage**: only the emulator build (and Pages deploy) is CI-checked; hardware behavior is not.
