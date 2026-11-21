# MixerLive

A small, dependable audio mixer for Windows, laid out like **Voicemeeter Banana**: 3 hardware + 2 virtual
input strips feeding 3 hardware (A1–A3) + 2 virtual (B1–B2) output buses, with gate, compressor, EQ,
routing buttons, faders, mute/solo/mono and OBS-style meters. It adds what Banana lacks:

* **WASAPI exclusive mode for hi-res DACs**, up to 32-bit 768 kHz (Banana's engine stops at 192 kHz),
* a **parametric EQ that loads EQ presets saved as .txt files** (from PEQdB, AutoEq, REW or Equalizer APO;
  Equalizer APO itself is not needed), on every output and on microphones,
* **KEYS**, a keyboard-click suppressor for microphones,
* **one-click import of your Voicemeeter Banana settings**,
* a sign-in task that Windows' *Startup apps* switch cannot silently turn off.

> **Note: No ASIO support.** MixerLive uses WASAPI exclusively rather than ASIO because ASIO is a proprietary
> API controlled by individual hardware vendors. WASAPI in exclusive mode delivers nearly identical performance,
> with a direct path to the DAC and no Windows shared-mode mixing or sample-rate conversion. It works with *any*
> Windows audio device without needing vendor-specific drivers. This is the same approach used by professional
> DAWs like Reaper and Studio One when ASIO drivers are unavailable.

It runs on Windows' own audio engine (WASAPI), so it installs no driver, and devices can be unplugged and
re-plugged while it runs.

> **Status: early release (0.1).** Tested and used daily on two Windows 11 machines running the latest 25H2 / 26H2 builds — desktop PC with PreSonus 24C XLR mixer + Audio-Technica AT-2040 microphone, gaming, OBS recording and voice chat; laptop (built-in mic to headphones). Other hardware has barely been tested yet, so please report problems under *Issues*.
>
> MixerLive is an independent project. It is **not affiliated with or endorsed by VB-Audio**, the maker of
> Voicemeeter and VB-CABLE.

### Hi-Res Audio & Fiio KA15

MixerLive's WASAPI exclusive mode unlocks audio quality that Windows' shared mode cannot provide. For example,
the **Fiio KA15** USB DAC is limited to 32-bit 384 kHz in Windows shared WASAPI mode, but MixerLive accesses
it at the full **32-bit 768 kHz** in exclusive mode. This applies to any high-end DAC that supports hi-res
formats above what Windows' shared mixer exposes.

| Mode | Sample Rate (Fiio KA15) | Latency | Quality Impact |
|---|---|---|---|
| Windows shared WASAPI | Up to 32-bit / 384 kHz | Higher (mixed by system) | Forced resampling, system mixer overhead |
| MixerLive exclusive WASAPI | Up to 32-bit / **768 kHz** | Lower (direct path) | Bit-perfect, no system mixing |

## Download and first start

**Requirements:** Windows 11 x64 (Windows 10 x64 should work but is untested). Nothing else to install:
the download contains everything it needs.

1. Download `MixerLive.exe` from the [latest release](../../releases/latest).
2. Put it in a folder where it can stay, for example `Documents\MixerLive\`. The sign-in task points at
   this location, so don't run it from the Downloads folder if you plan to delete files from there.
3. Double-click it. The program is not code-signed, so Windows may show *"Windows protected your PC"*:
   click **More info** → **Run anyway**. This appears once.
4. **Coming from Voicemeeter?** Close Voicemeeter first (tray icon → Exit). On first start MixerLive
   offers to import `Documents\Voicemeeter\VoicemeeterBanana_LastSettings.xml`, the file Banana rewrites
   with your current settings. You see a preview of every strip and bus, including whether each device was
   found, before anything is applied. You can repeat this at any time with **Import Voicemeeter settings…**
   (any saved `.xml` works).
5. Check the device boxes. Hardware inputs and A1–A3 come from the import; virtual input 1/2 default to
   `Voicemeeter Input` / `Voicemeeter AUX Input`; B1 defaults to VB-CABLE's `CABLE Input`.
6. Tick **Run at Windows sign-in** in the status bar. MixerLive then starts hidden in the tray at sign-in.
   Left-click the tray icon to open the mixer, close the window to hide it again.

Settings are saved automatically after every change to `%APPDATA%\MixerLive\`.

## Virtual devices: what you need

MixerLive does **not** install a virtual audio driver. It uses virtual devices that are already on your PC:

| What for | Device | Where it comes from |
|---|---|---|
| Apps and games play **into** the mixer (virtual inputs, e.g. a SYSTEM strip) | `Voicemeeter Input`, `Voicemeeter AUX Input` | Installing [Voicemeeter Banana](https://vb-audio.com/Voicemeeter/banana.htm). The devices keep working while Voicemeeter itself is closed: apps play into them and MixerLive picks the sound up by loopback. Any other virtual playback device works too. |
| Other apps **record** from the mixer (virtual outputs B1/B2, e.g. your processed microphone for Discord, games or OBS) | `CABLE Input` → apps record `CABLE Output` | [VB-CABLE](https://vb-audio.com/Cable/) (free), or any other virtual cable |

So if you are switching from Voicemeeter: **keep Voicemeeter installed, just don't run it.** Uninstalling
it removes `Voicemeeter Input`. VB-Audio's software is donationware; if you rely on their devices, consider
supporting them.

Keep Windows' *default playback device* set to `Voicemeeter Input` (as with Banana) so every app lands on
the SYSTEM strip, and send your real speakers or headphones from bus A1.

## What you get

| Area | MixerLive |
|---|---|
| Inputs | 3 hardware strips (any recording device) + 2 virtual strips (loopback of a playback device) |
| Outputs | A1–A3 to any playback device, B1–B2 to a virtual cable's playback side |
| Per strip | fader with a dB scale and editable dB and volume % boxes, mute, solo, mono, A1/A2/A3/B1/B2 routing, gate + compressor + parametric EQ + **KEYS** keyboard-click suppressor (hardware strips), bass/mid/treble (virtual strips), exclusive mode (hardware strips), level meter |
| Per bus | fader with a dB scale and editable dB and volume % boxes, DEVICE slider for the output device's own volume (A1–A3), mute, mono, exclusive mode, parametric EQ with preamp: 6 cells per channel in Voicemeeter's layout, up to 32 after importing an EQ file, level meter |
| Engine | 32-bit float processing at 44.1, 48, 96, 192, 384 or 768 kHz; sample rate, buffer size (in samples), latency and resampler all live in one **Audio settings…** window; drift-corrected ring buffers per device |
| Resampler | **Best**: 128-tap Kaiser-windowed sinc, passband flat to ±0.001 dB, images and aliases below −119 dB. **Fast**: 4-point Hermite for weak CPUs |
| Devices | hot-plug aware: a missing device shows *Waiting for device* and binds the moment it appears; a device that disappears or is taken by another program is re-bound automatically |
| Startup | **Run at Windows sign-in** registers a per-user Task Scheduler logon task (8 s delay, normal priority, no time limit) |
| Import | Voicemeeter Banana settings (`VoicemeeterBanana_LastSettings.xml` or any saved `.xml`): devices, labels, routing, faders, mute/solo/mono, gate, comp, strip EQ, bus EQ. EQ presets as .txt files (`Preamp:` / `Filter N: ON PK Fc … Gain … Q …` lines, as written by PEQdB, AutoEq, REW and Equalizer APO) |
| Profiles | **Save profile as…** exports the whole mixer plus the audio settings to one `.json` file; **Load profile…** loads it and asks before applying audio settings that differ from yours. Lock and tray preferences stay on the PC |
| Meters | OBS-style: a bar per channel from −60 to 0 dBFS over a dark green / yellow / red background (yellow from −20, red from −9), dB scale every 6 dB, a peak-hold line that stays for 2 s; the number on top is the loudest peak of the last 2 s and turns red at 0.0 dBFS or more (clipping). Strip meters are pre-fader, bus meters show what goes to the device; a bus without a device is not mixed and reads silence |
| Lock | **Lock** button in the top bar (and *Lock settings* in the tray menu), like Voicemeeter's *Lock GUI* |
| Footprint | mixer thread on a desktop Ryzen 9: 0.5 % of one core at 48 kHz, 3.6 % at 384 kHz, 7.5 % at 768 kHz with the Best resampler (3 % with Fast) |

### What it does not do

* No virtual audio driver of its own (see above), no ASIO driver API, VBAN, MIDI mapping, macro buttons,
  recorder, or Potato-only features. Exclusive-mode WASAPI reaches the same driver path as ASIO on USB DACs.
* Shared-mode end-to-end latency is about 60 ms (Low), 80 ms (Normal) or 150 ms (Safe). Exclusive mode
  removes the Windows mixer from a device's path and uses the driver's own 3–20 ms buffer.
* Don't run it at the same time as Voicemeeter: both would play to the same outputs. MixerLive shows a
  warning banner with a *Close Voicemeeter* button when it detects Voicemeeter running.

## Hi-res DACs, exclusive mode and headphone EQ

**Why exclusive mode exists.** Windows' Sound settings only offer shared-mode formats up to 384 kHz, and
some games break when the device they play to is set above 24-bit 192 kHz. Voicemeeter solved half of
that: games play to the virtual input at a safe format and Voicemeeter drives the DAC, but Voicemeeter's
own engine stops at 192 kHz. MixerLive keeps the game-safe virtual input and adds **EXCL** on each bus:
the bus opens the device in WASAPI exclusive mode and writes the driver's native PCM format directly, for
example 32-bit 768 kHz on a FiiO KA15, which Windows never offers in shared mode.

**Set-up for a USB DAC:**

1. **Audio settings…** (bottom left) → **Sample rate** → e.g. `768 kHz` → OK (the engine restarts). The DAC
   gets the engine rate in exclusive mode, so to run the DAC at 768 kHz the engine must run at 768 kHz.
2. Bus A1 → your DAC → **EXCL** on. The status line under the device box reports what the driver
   accepted, e.g. `exclusive · 32-bit PCM 768 kHz · 10 ms buffer`. If it says the device is in use, close
   whatever holds it.
3. Leave `Voicemeeter Input` in Sound settings at the format your games like (24-bit 48 or 192 kHz).
   MixerLive converts whatever comes in.
4. **Headphone EQ:** open **EQ…** on bus A1, click **Import EQ file (.txt)…** and pick the `.txt`
   preset for your headphones (any parametric EQ file, e.g. from PEQdB, AutoEq or REW). The preamp comes along. The EQ
   runs inside the mixer on that bus only, so a B1 recording stays untouched. If you used Equalizer APO
   before: exclusive streams bypass Windows' effect chain, so Equalizer APO cannot act on that device any
   more, and **Use active Equalizer APO config** copies whatever it currently runs (`config.txt` and its
   `Include:` files) into the bus EQ.

Honest notes: games, Spotify and Discord deliver 44.1/48 kHz audio, so the DAC plays upsampled material
either way; what changes is that the DAC runs at its native maximum and the Windows mixer is out of the
path. At 768 kHz Windows refuses to convert the loopback captures, so MixerLive resamples them itself
(see *Resampler quality*). Exclusive mode also means no other program can use that device while MixerLive
holds it; if another program takes it (e.g. a player in ASIO/exclusive mode), the bus goes silent and
re-binds a few seconds after the device is free again.

**Hardware inputs** have the same **EXCL** button: the microphone is read in its native format with the
driver's smallest buffer (loopback strips cannot be exclusive, Windows does not allow it).

## Microphone: EQ, KEYS and sending it to games

Hardware input strips have the same parametric EQ as the buses (**EQ** on/off, **EQ…** to edit it or load
a `.txt` EQ preset). So a microphone can have its own EQ while the headphone output
keeps a different one. With **MONO** on, the left cells shape the whole signal.

**KEYS** is a keyboard-click suppressor. A keystroke is a burst of high-frequency sound that rises within
a fraction of a millisecond, much faster than speech; the suppressor listens for that and turns the input
down by 24 dB for a few hundredths of a second each time, with 3 ms of look-ahead so the click's start is
caught too. It is a transient suppressor, not a trained noise model: chat partners hear far less typing,
but hard consonants (t, k, s) spoken while typing can get clipped a little. It runs before the gate, so
the gate no longer opens on keystrokes.

Games and chat programs take the microphone from Windows, not from MixerLive. For them to hear the
processed microphone, send it through a virtual cable: install VB-CABLE, set bus **B1** to `CABLE Input`,
light **B1** on the microphone strip (and turn off A1 unless you want to hear yourself), then pick
`CABLE Output` as the microphone in the game, Discord or OBS.

## Volume controls

| Control | What it changes | Notes |
|---|---|---|
| Fader + **dB** box | the mixer's gain for that strip or bus | −inf (off) … +12 dB. Type a value and press Enter, or use the mouse wheel / ↑ ↓: ±1 dB, Ctrl ±0.1 dB. Double-click the fader for 0 dB. |
| **%** box next to it | the same gain, shown as loudness % | 100 % = 0 dB, 50 % = half as loud (−10 dB), 25 % = −20 dB, 10 % ≈ −33 dB, 0 % = off, up to 230 % (+12 dB). Wheel / ↑ ↓: ±2 %, Ctrl ±0.5 %. |
| **DEVICE** slider (A1–A3) | the output device's own volume, the same slider as in Windows Sound settings | On USB DACs this is usually the DAC's hardware volume, so it also works in exclusive mode. Greyed out when a device without hardware volume runs in exclusive mode. |

Windows' volume keys and taskbar slider act on the *default playback device*, normally `Voicemeeter
Input`, not on the device behind bus A1. Use the bus controls for the headphones or speakers.

## Audio settings: rate, buffer size and latency

All engine settings live in one place, **Audio settings…** at the bottom left, laid out like a sound
card's control panel:

| Setting | What it does |
|---|---|
| **Sample rate** | the rate the mixer runs at (44.1 … 768 kHz); match your DAC's exclusive-mode rate |
| **Buffer size** | audio the mixer handles at a time, in samples (power-of-two sizes between 1 and 25 ms, plus the 5 ms default) |
| **Latency** | safety margin for every device: Low = least delay, Normal, Safe = fewest crackles |
| **Resampler** | Best (recommended) or Fast, see *Resampler quality* |

A status box shows the resulting input and output latency of every open device in samples and
milliseconds. A smaller buffer or Latency Low means less delay but a higher chance of crackles; what works
depends on your hardware. **Show expert settings** exposes the individual buffers that Latency chooses:

| Setting | What it is | Preset Low / Normal / Safe |
|---|---|---|
| Input buffer | captured audio kept ahead of the mixer, per input strip | 12 / 20 / 40 ms |
| Output buffer | mixed audio kept ahead of each output stream | 20 / 25 / 50 ms |
| Windows output buffer | buffer requested from Windows for shared-mode outputs | 15 / 20 / 40 ms |
| Device buffer (exclusive) | buffer requested from the driver for EXCL inputs and outputs; the driver's minimum wins if larger | ≥ 3 / ≥ 10 / ≥ 20 ms |

Input latency ≈ the device's buffer + input buffer + mixer block; output latency ≈ output buffer + the
device's buffer; driver and USB delays come on top. Shared-mode inputs always arrive in 10 ms packets, so
an input buffer below about 12 ms only helps with EXCL inputs. All of this restarts the engine, is saved
with your settings and travels inside exported profiles.

## Resampler quality

Every device runs on its own clock, so every stream passes through an interpolator that trims ±0.2 % of
drift and, when a device cannot run at the engine rate, converts the rate.

| | Best (default) | Fast |
|---|---|---|
| Method | 128-tap Kaiser-windowed sinc (β = 10), 128 phases with linear blending, SIMD over stereo frames; kernel stretched when down-sampling so it also anti-aliases | 4-point Hermite cubic |
| Passband at 48 kHz, 1 / 15 / 19 kHz | 0.000 / 0.000 / −0.001 dB | 0 / −0.3 / −2.8 dB |
| Images when up-sampling 48 → 192 kHz | −119 to −124 dB | −29 to −56 dB |
| Alias of a 60 kHz tone when down-sampling 192 → 48 kHz | −124 dB | 0 dB (no filter) |
| CPU, mixer thread, 5 streams at 768 kHz | 7.5 % of one core | 3 % |

The figures come from `tests/MixerLive.Tests/ResamplerTests.cs`, which runs sine tones through the real
code path and reads levels with a windowed single-bin DFT.

## Locking the mixer

Click **Lock** in the top bar, or tick *Lock settings* in the tray menu. While locked, strips and buses
ignore clicks, the mouse wheel and the keyboard; *Stop engine*, the import/load buttons, *Audio settings…*
and the status-bar check boxes are greyed out. Meters, *Restart engine*, *Save profile as…*, *Logs* and
*Exit* keep working. The lock is saved, so the mixer comes back locked after a restart.

## Troubleshooting

* **A strip or bus says "Cannot open …"**: another program has the device in exclusive mode (Voicemeeter in
  KS/ASIO mode does this), or exclusive mode is disabled in the device's Windows properties. MixerLive
  retries every few seconds.
* **Crackles**: set **Latency** to *Safe* or pick a bigger **Buffer size** in Audio settings, or check the
  per-channel *buffer resyncs* counter under the fader: a steadily rising count means the device clock is
  wandering more than 0.2 %.
* **No sound from apps**: make sure Windows' default playback device is `Voicemeeter Input` and that the
  SYSTEM strip has A1 lit; the SYSTEM meter should move when something plays.
* **Equalizer APO's headphone EQ stopped working after turning on EXCL**: expected, load the same `.txt`
  into the bus EQ (see above).
* **Doesn't start at sign-in**: open Task Scheduler, task *MixerLive*; it runs under your account 8 seconds
  after sign-in. If you moved `MixerLive.exe`, untick and re-tick **Run at Windows sign-in**.
* **Voicemeeter's own "run at startup" stopped working at some point**: Windows 11 can switch startup
  shortcuts off in *Settings → Apps → Startup* (and some updates reset that list). MixerLive avoids this
  by using a Task Scheduler task instead.
* Logs: `%APPDATA%\MixerLive\logs\` (button **Logs** in the status bar). Please attach the day's log when
  reporting a problem.

## Uninstalling

1. Untick **Run at Windows sign-in** (this removes the sign-in task).
2. Exit MixerLive from the tray icon and delete `MixerLive.exe`.
3. Optionally delete `%APPDATA%\MixerLive\` (settings, profile and logs).

## Command line

| Switch | Effect |
|---|---|
| `--minimized` | start in the tray without showing the window (used by the sign-in task) |
| `--import <file.xml>` | import a Voicemeeter settings file, save it as the active profile, then start |
| `--profile <file.json>` | start with a specific MixerLive profile instead of the saved one |
| `--rate <hz>` | engine sample rate for this session (not saved unless changed in the UI) |
| `--resampler fast\|best` | interpolator for this session |
| `--block <ms>` | mixer block length for this session (1–50 ms) |
| `--lock` | start with the mixer locked (saved only if you change the lock in the UI) |
| `--selftest [seconds]` | no UI: open all devices, run the engine, print a per-channel report, exit (also written to `%APPDATA%\MixerLive\selftest-report.txt`) |
| `--test-instance` | run next to an already running MixerLive; the saved profile and settings are never written |
| `--snapshot <file.png>` | debug: render the window off-screen to a PNG after 5 s and exit (implies `--test-instance`) |
| `--open-audio-settings` | debug: with `--snapshot`, also render the Audio settings window to `<file>.audio-settings.png` |
| `--open-eq <0-4>` | debug: open the EQ window of bus A1..B2 after start |

## Building from source

Needs the [.NET 10 SDK](https://dotnet.microsoft.com/download).

* `build-release.cmd` builds the self-contained release `dist\MixerLive.exe`.
* `dotnet build` builds everything; `dotnet test` runs the unit tests; `MixerLive.exe --selftest 10`
  exercises the real audio path on your devices.

### How it works

MixerLive implements a real-time audio engine from scratch in C# / .NET 10, demonstrating several advanced
  systems programming techniques:
  * **Lock-free ring buffers** for zero-copy data transfer between audio threads without mutex contention
  * **PI controller-based clock drift correction** that learns and compensates for hardware clock differences
    between devices (±0.2 % max), maintaining sync at both 48 kHz and 768 kHz
  * **MMCSS "Pro Audio" thread scheduling** with high-resolution waitable timers, no busy-waiting, no CPU waste
  * **Windowed sinc and Hermite resampling** for high-quality sample-rate conversion when devices differ in rate
  * **Device-change notification system** (no polling) that detects hot-plugged/unplugged devices instantly
  * **Tolerant XML parsing** for robust Voicemeeter settings import from potentially malformed files

```
mic / line in ──WASAPI capture──┐                              ┌──WASAPI render──▶ A1  DAC (shared or exclusive)
Voicemeeter Input ──loopback────┤  ring buffers with drift     │                   A2  2nd device
Voicemeeter AUX Input ──loopback┤  correction (±0.2 % max)     ├──WASAPI render──▶ A3
                                ▼                              │
                     mixer thread (MMCSS "Pro Audio")          ├──WASAPI render──▶ B1  CABLE Input
                     strip DSP → routing matrix → bus DSP ─────┘                   B2
```

* The mixer thread (5 ms blocks by default) is paced by a high-resolution waitable timer, never by
  busy-waiting: it runs at MMCSS "Pro Audio" priority, and a spinning real-time thread would steal time
  from games and from Windows' own audio engine. Devices that are not connected are bound through
  device-change notifications (plus a probe every 2 minutes), not by polling.
* Every device gets its own thread and WASAPI stream. Shared mode asks Windows to convert to float32 at the
  engine rate (`AUTOCONVERTPCM`), with a software fallback when the driver refuses. Exclusive mode probes
  the driver for the widest PCM format at the engine rate (32-bit, 24-in-32, 24, 16-bit; stereo then
  mono), falls back through 768 kHz … 8 kHz, and honours WASAPI's buffer-alignment and device-period rules.
* Each stream is decoupled through a lock-free ring buffer whose reader resamples (windowed sinc or
  Hermite) under a slow PI controller (in milliseconds, so it behaves the same at 48 and 768 kHz) that
  learns the clock drift between devices. Nothing stalls when a device disappears.
* DSP: RBJ biquads (EQ), stereo-linked gate with hold, feed-forward soft-knee compressor with auto
  make-up, transient (keyboard-click) suppressor with look-ahead, click-free gain ramps. EQ cells use
  Voicemeeter's type numbering; Equalizer APO's PK/LS/LSC/HS/HSC/LP/HP/NO/BP map onto them.
* The Voicemeeter importer is a tolerant tag scanner rather than a strict XML parser because Banana's
  auto-saved file is not always well-formed (it can contain a duplicated opening tag).

```
src/MixerLive.Core    engine (Audio/), DSP (Audio/Dsp/), model + JSON store (Model/),
                      Voicemeeter + Equalizer APO importers (Import/), logon task (Startup/), Voicemeeter detection (Compat/)
src/MixerLive.App     WPF UI, tray icon, dialogs
tests/MixerLive.Tests xunit: importers, device-name matching, ring/drift control, resampler, DSP, meters, settings
```

## License and credits

MixerLive is released under the [MIT License](LICENSE). It uses [NAudio](https://github.com/naudio/NAudio)
(MIT) and the .NET runtime (MIT); see [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
