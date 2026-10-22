# Feature Specification: DSP Engine, Mixer & Synthesizer Performance Completion

**Feature Branch**: `037-dsp-synth-engine-perf`

**Created**: 2026-10-22

**Status**: Draft

**Input**: User description: "Przeanalizuj czy silnik DSP jest dobrze zoptymalizowany, łącznie z syntezatorami typu Aurora, napisz sensowny plan optymalizacji i spec który zaplanuje naprawę zgodnie ze skillem."

## Nota Context *(fill in — drives plan and tasks)*

**Feature area** *(pick the main one)*:

- [x] Engine (C++, `src/native/nota.engine`) — real-time audio rules apply, see `ARCHITECTURE.md` AR table
- [ ] Managed app (C#, `src/managed/*`) — domain, application, infrastructure, presentation
- [ ] UI (Avalonia, `src/managed/Nota.Presentation`) — follow `DESIGN.md` (Studio)
- [ ] Plugin hosting (`pluginhost/`) — the only place JUCE is allowed
- [ ] Build / packaging / CI (`scripts/`, `.github/`) — see `BUILD.md`
- [ ] Docs only

**Nota docs to keep in mind** *(links relative to `specs/<slug>/`, where this file lands)*:
[`ARCHITECTURE.md`](../../ARCHITECTURE.md) (AR rules),
[`CONTRIBUTING.md`](../../CONTRIBUTING.md) (licensing, dependency policy, PR
expectations), [`BUILD.md`](../../BUILD.md), [`DESIGN.md`](../../DESIGN.md),
[`FEATURES.md`](../../FEATURES.md).

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Idle and silent instrument tracks consume near-zero CPU (Priority: P1) 🎯 MVP

A producer loads a large arrangement (e.g., 32 tracks of Nota Aurora, Volt, Pentad, Consort, Operator, Flux, Bass, and Racks) where most tracks are silent during any given bar. Once a track's voices and internal FX tails (chorus, reverb, delay) have completely decayed to silence and its `isIdle()` state engages, the engine and the instrument skip sample-by-sample rendering until a note-on, note-off, expression event, or parameter edit wakes the instrument on the exact sample of the event.

**Why this priority**: Currently, 8 built-in synthesizers (including Nota Aurora) compute `idle_ = true` at the end of `render()`, and their comments state *"the engine skips render while idle"*, yet neither `Engine_Render.cpp` (`playEvents`), `RackInstrument.cpp`, `DrumRack.cpp`, nor the synthesizers' own `render()` entry points check `isIdle()` / `idle_`. As a result, a silent Aurora track continuously runs its 4-tap stereo chorus LFO and 4-comb/2-allpass Freeverb tank on zeros every block. Fixing this dead contract eliminates ~95–100% of CPU cost on silent instrument tracks with zero sonic change (bit-identical).

**Independent Test**: Render the `silent-32` and `sparse-32` scenarios in `nota_perf` and run `DspSilenceBypassTests` extended with Aurora, Volt, Pentad, and Rack instruments. Verify that silent blocks cost near-zero instructions/wall-time and that waking on a note-on is sample-accurate, click-free, and bit-identical (`< -120 dB` residual) after quiescent tails.

**Acceptance Scenarios**:

1. **Given** an instrument track (Aurora, Volt, Pentad, Consort, Operator, Flux, Bass, Synth, or an Instrument/Drum Rack) whose voices and internal tails have decayed past the hangover period (`isIdle() == true`), **When** a block with no MIDI/expression events is rendered, **Then** the instrument's per-sample synthesis and internal FX loops are skipped and the output buffer remains silent.
2. **Given** a sleeping instrument (`isIdle() == true`) with a note-on or expression event at sample offset `k` within the block, **When** `playEvents` processes the block, **Then** samples `[0, k)` stay silent without running the synth loop, the event wakes the instrument at offset `k`, and samples `[k, frames)` render identically to a never-slept instance.
3. **Given** an instrument with a time-varying LFO or delay phase that must stay continuous across sleep (e.g., Aurora LFO/chorus phase, Consort latched sequencer), **When** skipping `render()` while `idle_`, **Then** its phase/clock advances by `frames` in $O(1)$ arithmetic so wake-up phase is preserved.

---

### User Story 2 - Nota Aurora heavy polyphonic patches run with lower CPU and bit-identical sound (Priority: P1)

A musician playing dense polyphonic chords on Nota Aurora (such as `aurora-supersaw-2x` with 2 oscillators × 7 unison voices + stereo SVF filters + drive + 4-voice chorus + reverb) experiences significantly lower CPU usage and higher polyphony headroom, while existing projects and factory presets render bit-identically (`< -120 dB` residual).

**Why this priority**: Profiling `aurora-supersaw-2x` shows `WavetableSynth::oscStack` and `WavetableSynth::chorus` as the top CPU hotspots. Inside `oscStack`, `readMorph` calls `std::floor(x)` on a non-negative phase index 84 times per sample (for 6 voices × 14 unison oscillators), and `warpRead` is dispatched inside the unison loop even when warp mode is Off (`0`) or Fold (`4`). Inside `chorus`, up to 4 calls to `std::sin` and modulo `% sz` divisions run every sample even though the 1, 2, or 4 tap phases sit at exact quarter-turn offsets ($0, \frac{1}{4}, \frac{1}{2}, \frac{3}{4}$) of a single LFO angle and the buffer can use a power-of-two bitmask.

**Independent Test**: Run `nota_perf run aurora-supersaw-2x` and `nota_perf run instrument-05` before and after; run `AuroraNullTests` (`--aurora-null` and `--aurora-null-compare`) across all factory Aurora presets asserting `< -120 dB` residual.

**Acceptance Scenarios**:

1. **Given** `WavetableSynth::readMorph` and `WavetableSynth::oscStack`, **When** rendering any factory preset, **Then** non-negative table index truncation avoids `std::floor`, `warpRead` is bypassed when `mode == 0` or `mode == 4`, and the offline null test against the pre-change engine measures `< -120 dB` across all factory presets.
2. **Given** `WavetableSynth::chorus` with 1, 2, or 4 voices, **When** rendering audio, **Then** the circular buffer uses power-of-two bitmask indexing (`& mask` instead of `% sz` and `while (rp < 0)`) and tap LFOs derive their $\sin$ offsets from a single trigonometric evaluation per sample while keeping null residual `< -120 dB`.
3. **Given** `WavetableSynth::render`, **When** all voices are inactive (`live == 0`) during the hangover period and both Chorus and Reverb are switched off or at zero amount, **Then** the per-sample loop exits early rather than iterating `frames` times over inactive voices.

---

### User Story 3 - Vectorized SIMD modal resonators in Physical & Keys and stereo SVF in Aurora & Volt (Priority: P2)

A musician playing polyphonic passages on Nota Physical, Nota Keys, Nota Aurora (with stereo unison spread), or Nota Volt (with stereo unison) benefits from hardware SIMD vectorization (ARM NEON and x86 SSE2/AVX) on the innermost filter and resonator loops without audible change (`< -90 dB` null residual).

**Why this priority**:
- `PhysicalSynth::bank` and `Keys::renderVoice` evaluate 16 independent 2nd-order modal resonators per voice per sample in a scalar loop. Because the 16 modes have no cross-mode data dependencies within a sample and are already stored in Structure-of-Arrays (`a1[16], a2[16], b0[16], y1[16], y2[16]`), processing 4 modes per SIMD instruction yields a 3×–3.5× speedup on modal synthesis.
- In `WavetableSynth` (`stereoUni`) and `VoltSynth` (`stereo`), Mid and Side (or L and R) signals pass through identical SVF filter coefficients (`a1, a2, a3, k`). Filtering both channels simultaneously in a 2-lane SIMD vector cuts stereo filter cost in half.

**Independent Test**: Run `nota_perf run instrument-02` (Physical), `instrument-16` (Keys), `instrument-05` (Aurora), `instrument-06` (Volt), and verify sound preservation (`< -90 dB` residual) and stability in `tests/Nota.SmokeTest`.

**Acceptance Scenarios**:

1. **Given** `PhysicalSynth::bank` / `bankSerial` and `Keys::renderVoice`, **When** processing the 16 modal resonators for a voice, **Then** modes are evaluated in 4-wide SIMD vectors (`float32x4_t` / `__m128` or `float64x2_t` / `__m128d`) with horizontal reduction, matching the scalar reference within `< -90 dB` residual.
2. **Given** `WavetableSynth::filterPair` when `stereoUni` is active (and `VoltSynth` when `stereo` is active), **When** filtering the mid/side (or L/R) pair with shared SVF coefficients, **Then** both channels are processed together in a paired 2-channel pass rather than two separate sequential filter traversals.

---

### User Story 4 - Vectorized mixer metering, master bus SIMD pass & proactive multi-core parallelism (Priority: P2)

A producer playing back a multi-track session experiences lower mixer CPU overhead and zero cold-start audio stutter when pressing Play on a heavy multi-track arrangement.

**Why this priority**:
1. `AudioBufferUtils.h` already defines single-pass SIMD routines (`scaleStereoMeter`, `addStereoScaledMeterMap`, `processMasterBus`), but `Engine_Render.cpp` still executes scalar sample-by-sample loops for leaf/group/return metering (lines `2332-2341`, `2436-2444`, `2504-2514`) and for the master bus volume, `std::isfinite` check, `std::clamp`, and stereo correlation accumulation (lines `1801-1820`).
2. In `Engine_Render.cpp:2204-2263`, live-device multi-threading (`rtParallel_`) stays off (`helpers = 0`) until a callback *exceeds* `1.0 * periodNs` — meaning the engine waits for an overload/xrun before waking worker threads. Engaging `rtParallel_` proactively when single-thread leaf work exceeds **65% of the buffer period** (`0.65 * periodNs`) prevents the first overload dropout while keeping light sessions on a single thread.

**Independent Test**: Run `nota_perf` on `mix-8`, `mix-16`, `mix-32`, `sparse-32`, `silent-32`, and run `RenderPoolTests`, `PanLawTests`, `StereoMeasureTests`, and `DspOptimizationTests` in `tests/Nota.SmokeTest`.

**Acceptance Scenarios**:

1. **Given** `Engine::processBlock` master output stage, **When** scaling by `masterVolume_`, suppressing non-finite values, clamping to `[-1, 1]`, and computing peak/RMS/stereo-correlation meters, **Then** it executes via `processMasterBus` in a single SIMD pass.
2. **Given** leaf tracks, group tracks, and return buses in `Engine::mixGraph` when `tapRec` is false and pan law is static (`!ps.cross`), **When** scaling/summing and computing channel meters, **Then** it uses vectorized `StereoMeterAccumulator` / `SimdAudio` routines instead of scalar per-sample loops.
3. **Given** a live audio callback (`!offline`) with `m >= 2` eligible tracks and `frames >= 128`, **When** measured leaf render work exceeds `0.65 * periodNs`, **Then** `rtParallel_` engages before `work` crosses `1.0 * periodNs` (preventing the first xrun).

---

### Edge Cases

- **Mid-block MIDI wake-up**: A sleeping instrument (`isIdle() == true`) receives a `noteOn`, `noteOff`, controller, or MPE expression event at sample offset `128` of a `512`-frame block. `playEvents` must advance any free-running phase over `[0, 128)` (or leave `dst[0..255]` at zero), wake the instrument at `128`, and call `inst.render(&dst[128 * 2], 384)`.
- **Free-running clocks while asleep**: `Keys` (tremolo/phaser/chorus LFOs), `RhythmMachine` (step sequencer), `Mosaic` (`clock_`), and `WavetableSynth` (LFO1/LFO2 and chorus LFO phases) must keep their phase/clock continuous across skipped idle blocks so waking up on a later bar has no phase jump.
- **Internal resampling (`tapRec == true`)**: When a track is the source for internal resampling (`w.tapRec == true`), its post-fader frames must still be pushed into `inputQueue_` in exact sample order.
- **Non-finite values on the master bus**: `processMasterBus` must squash `NaN`, `+Inf`, and `-Inf` to `0.0f` before clamping to `[-1.0f, 1.0f]`, matching the existing master safety contract.
- **Parameter lookup overhead on preset load**: `VoltSynth`, `Pentad`, `OperatorSynth`, `FluxSynth`, and `BassSynth` should use a static `std::unordered_map<std::string, int32_t>` in `pluginParamIndexOfId()` (matching `WavetableSynth` and `Consort`), eliminating $O(N^2)$ string allocations during preset/automation lookup.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: All built-in instruments implementing `isIdle()` (`WavetableSynth`, `VoltSynth`, `Pentad`, `Consort`, `OperatorSynth`, `FluxSynth`, `BassSynth`, `Synth`, `PhysicalSynth`, `GrainSynth`, `Keys`, `Sampler`, `Mosaic`, `RhythmMachine`) MUST early-return in `render()` when `idle_` is `true` (advancing only free-running phases/clocks in $O(1)$ time), and `RackInstrument` / `DrumRack` MUST skip idle chains whose instrument and chain devices all report `isIdle() == true`.
- **FR-002**: `WavetableSynth::readMorph` MUST avoid `std::floor` on non-negative table indices, and `WavetableSynth::oscStack` MUST hoist `mode == 0` (Warp Off) and `mode == 4` (Fold) out of the per-unison-voice loop while remaining bit-identical (`< -120 dB` residual).
- **FR-003**: `WavetableSynth::chorus` MUST use a power-of-two circular buffer with bitmask wrapping and compute 1-, 2-, or 4-voice quarter-phase LFO taps from a single trigonometric evaluation per sample while keeping factory preset null tests `< -120 dB`.
- **FR-004**: `PhysicalSynth` (`bank` and `bankSerial`) and `Keys` (`renderVoice` modal loop) MUST evaluate their 16 modal resonators using 4-wide SIMD vector instructions (`NOTA_SIMD_NEON` / `NOTA_SIMD_SSE`) with aligned state arrays, keeping null residual `< -90 dB`.
- **FR-005**: `WavetableSynth::filterPair` (when `stereoUni` is active) and `VoltSynth` (when `stereo` is active) MUST process paired Mid/Side (or L/R) channels through shared SVF coefficients in a unified 2-channel pass.
- **FR-006**: `Engine_Render.cpp` MUST use `processMasterBus` for master bus gain/NaN-squash/clip/metering and `StereoMeterAccumulator` SIMD passes for static-pan leaf, group, and return metering (when `!tapRec`), and `Convolution.cpp` / `Chamber.cpp` MUST vectorize `cmac` complex frequency-domain accumulation.
- **FR-007**: `Engine_Render.cpp` MUST engage `rtParallel_` proactively when single-thread leaf work exceeds `0.65 * periodNs` (with `frames >= 128` and `m >= 2`), rather than waiting for `work > periodNs`.
- **FR-008**: `VoltSynth`, `Pentad`, `OperatorSynth`, `FluxSynth`, and `BassSynth` MUST use a static `std::unordered_map<std::string, int32_t>` in `pluginParamIndexOfId()`.
- **FR-009**: All changes MUST strictly obey `AR-6` (zero heap allocations, zero locks, zero blocking I/O on the audio thread) and `AR-7` (pure C ABI untouched).

### Key Entities

- **Instrument Auto-Sleep State (`idle_`, `quietSamples_`)**: Per-instrument state tracking whether all voices and internal FX tails have decayed below $-100\text{ dBFS}$ (`1.0e-5f`) for at least `kIdleSamples`, allowing `render()` and rack chains to bypass DSP work while keeping free-running clocks synchronized.
- **SIMD Modal Resonator Bank (`PhysicalSynth::Bank`, `Keys::Voice`)**: 16-mode 2nd-order IIR resonator bank aligned to 16-byte SIMD boundaries (`alignas(16)`), evaluated 4 modes per vector instruction.
- **SIMD Stereo Meter & Master Processor (`StereoMeterAccumulator`, `processMasterBus`)**: Single-pass NEON/SSE vector routines computing peak L/R, sum-of-squares L/R, and stereo cross-product L·R alongside gain scaling and branchless non-finite suppression.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: `nota_perf run silent-32` and idle instrument tracks (including Aurora, Volt, Pentad, and Racks after tail decay) show at least **70% reduction** in render work/wall-time once `idle_` engages.
- **SC-002**: `nota_perf run aurora-supersaw-2x` and `instrument-05` wall-time and instruction count improve by at least **15%–25%** on top of the `036-aurora-perf` Tier-1 baseline.
- **SC-003**: `nota_perf run instrument-02` (Physical) and `instrument-16` (Keys) render time improves by at least **30%–50%** via SIMD modal bank evaluation.
- **SC-004**: All factory Aurora presets pass `AuroraNullTests` (`< -120 dB` for Tier-1 changes; `< -90 dB` for any paired-SVF/SIMD change), and all tests in `tests/Nota.SmokeTest` pass 100% green via `scripts/build.sh`.
- **SC-005**: Zero audio-thread allocations, locks, or `AR-6` violations across all modified files.

## Assumptions

- Target platforms are ARM64 (`NOTA_SIMD_NEON`) and x86_64 (`NOTA_SIMD_SSE` / `NOTA_SIMD_AVX`).
- Power-of-two chorus buffer sizing in `WavetableSynth` (`4096` at $\le 48\text{ kHz}$, `8192` at $\le 96\text{ kHz}$, `16384` at $192\text{ kHz}$) covers the maximum chorus delay depth ($\le 14\text{ ms}$) with ample headroom and is allocated only in the constructor and `setSampleRate()`.
- Advancing LFO phases (`lfoPhase1_`, `lfoPhase2_`, `chorusPh_`) by `frames * inc` on idle skip in `WavetableSynth` keeps free-running modulation phases identical to active rendering.

## Nota Compliance Checklist *(review before implementation; re-check in tasks.md)*

- [x] Every new source file will carry the AGPL-3.0-only SPDX header (two lines, see CONTRIBUTING.md)
- [x] No new dependency — or: license checked, AGPL-compatible, `LICENSES/third-party.md` row planned
- [x] Audio-thread impact considered: no alloc / lock / I/O on the audio thread (AR-6), command queue for data in (AR-4), immutable snapshots for structural edits (AR-5)
- [x] Managed/native crossings go through the pure C ABI in `nota_engine.h` only (AR-7); no JUCE outside `pluginhost/`
- [x] Smoke-test impact assessed: engine behaviour change → extend `tests/Nota.SmokeTest`
- [x] User-visible change → `CHANGELOG.md` `[Unreleased]` entry planned (and FEATURES.md / user docs via the nota-features / nota-docs skills when working with an agent that has them)
- [x] UI change → follows `DESIGN.md` (Studio design language) (N/A — engine-only)
