# Data Model: DSP Engine, Mixer & Synthesizer Performance Completion

**Feature**: `037-dsp-synth-engine-perf` | **Date**: 2026-10-22

No persistent project format or C ABI struct changes. This document specifies the internal real-time DSP state invariants and SIMD memory layouts.

---

## 1. Instrument Auto-Sleep State Machine (`Instrument::isIdle()`)

Applies to all built-in `Instrument` subclasses (`WavetableSynth`, `VoltSynth`, `Pentad`, `Consort`, `OperatorSynth`, `FluxSynth`, `BassSynth`, `Synth`, `PhysicalSynth`, `GrainSynth`, `Keys`, `Sampler`, `Mosaic`, `RhythmMachine`).

### State Transitions
- **Active (`idle_ == false`)**:
  - Entered on construction, `noteOn()`, `noteOff()`, `allNotesOff()`, `pluginParamSet()`, `controller()`, `setSampleRate()`, `setState()`, or `wake()`.
  - `render(out, frames)` executes full voice + internal FX synthesis.
  - At the end of `render()`, if `activeVoices == 0` (and any internal sequencer/hold state is inactive) AND the block's peak output `<= 1.0e-5f` ($-100\text{ dBFS}$), `quietSamples_ += frames`; otherwise `quietSamples_ = 0`.
  - When `quietSamples_ > flightSamples + kIdleSamples`, `idle_` transitions to `true`.
- **Sleeping (`idle_ == true`)**:
  - `render(out, frames)` returns immediately in $O(1)$ time without touching `out` (which the caller already zeroed) after advancing any free-running phase/clock by `frames`:
    - `WavetableSynth`: advances `lfoPhase1_`, `lfoPhase2_`, `chorusPh_`, and `chorusW_`.
    - `VoltSynth`: advances `lfoPhase1_`, `lfoPhase2_`, and `vibPhase_`.
    - `Pentad`: advances `lfoPh_` and `tFrames_`.
    - `Consort`: advances `lfoPh_` and `tFrames_` (sleep is blocked while `seqRun_ == true`).
    - `FluxSynth`: advances `motionPhase_` and `driftPhase_`.
    - `BassSynth`: advances `lfoPhase_`.
- **Note Expression Wake Rule**:
  - In `noteExpression(pitch, dim, value)`: if `activeVoices > 0` or `pitch < 0` (global wheel/pressure), call `wake()` so expression changes on a held or decaying voice are never missed.

---

## 2. `WavetableSynth` Power-of-Two Chorus Buffer & Quarter-Wave LFO

- **Fields**:
  - `std::vector<float> chorusBuf_`: sized to the smallest power of two $\ge \lceil 0.05 \times \text{sampleRate} \rceil + 4$ (e.g., `4096` at $44.1/48\text{ kHz}$, `8192` at $88.2/96\text{ kHz}$).
  - `int chorusMask_ = (int)chorusBuf_.size() - 1`: bitmask replacing modulo `% sz` and `while (rp < 0)`.
- **Quarter-Turn Tap Identity**:
  - Given base phase $\phi = \text{chorusPh\_} \in [0, 1)$, let $s = \sin(2\pi \phi)$:
    - `voices == 1`: tap 0 uses $s$.
    - `voices == 2`: tap 0 uses $s$, tap 1 uses $-s$.
    - `voices == 4`: let $c = \cos(2\pi \phi)$; taps $0..3$ use $\{s, c, -s, -c\}$.

---

## 3. SIMD Modal Resonator Layout (`PhysicalSynth` & `Keys`)

### `PhysicalSynth::Bank` (2-wide `double` SIMD: `float64x2_t` / `__m128d`)
```cpp
struct Bank {
    static constexpr int kModes = 16;
    alignas(16) double a1[kModes] = {};
    alignas(16) double a2[kModes] = {};
    alignas(16) double b0[kModes] = {};
    alignas(16) double bs[kModes] = {};
    alignas(16) double y1[kModes] = {};
    alignas(16) double y2[kModes] = {};
    double R[kModes] = {}, w[kModes] = {}, amp[kModes] = {};
    float bowNorm = 0.0f;
};
```
- **Invariants**: `kModes == 16` is divisible by 2 (NEON `float64x2_t` / SSE2 `__m128d`). Inactive modes above Nyquist have `a1 = a2 = b0 = bs = y1 = y2 = 0.0`.

### `Keys::Voice` (4-wide `float` SIMD: `float32x4_t` / `__m128`)
```cpp
alignas(16) float a1[kMaxModes] = {};
alignas(16) float a2[kMaxModes] = {};
alignas(16) float b[kMaxModes] = {};
alignas(16) float y1[kMaxModes] = {};
alignas(16) float y2[kMaxModes] = {};
```
- **Invariants**: `kMaxModes == 16` is divisible by 4. Modes `m >= nModes` have `a1[m] = a2[m] = b[m] = y1[m] = y2[m] = 0.0f`, so iterating over `m = 0, 4, 8, 12` (or up to `(nModes + 3) & ~3`) produces bit-exact zero contributions from unused slots.

---

## 4. Paired 2-Channel TPT SVF State (`WavetableSynth` & `VoltSynth`)

When filtering two channels (Mid + Side in `WavetableSynth`, Left + Right in `VoltSynth`) through identical `SvfCoef { a1, a2, a3, k }`:
- Both channels' integrator states `(ic1_ch0, ic1_ch1)` and `(ic2_ch0, ic2_ch1)` are updated in a single function call `svfStereo` / `filterPairStereo` using 2-wide `double` SIMD (`float64x2_t` / `__m128d`) and a single `switch (type)` branch.
