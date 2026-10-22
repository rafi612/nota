# Research: DSP Engine, Mixer & Synthesizer Performance Completion

**Feature**: `037-dsp-synth-engine-perf` | **Date**: 2026-10-22

All technical decisions below were verified directly against the current C++20 engine sources in `src/native/nota.engine/`.

---

## R1: Why do idle synthesizers (including Nota Aurora) still consume CPU?

- **Finding**:
  - In `WavetableSynth.cpp:580-589`, `VoltSynth.cpp:574`, `Pentad.cpp:318`, `Consort.cpp:302`, `OperatorSynth.cpp:305`, `FluxSynth.cpp:273`, `BassSynth.cpp:316`, and `Synth.cpp:273`, each synth computes `idle_ = true` at the bottom of `render()` and comments *"the engine skips render while idle"*.
  - However, `Engine_Render.cpp` (`playEvents`, lines `160-211`) **never checks `inst.isIdle()`**, and unlike `PhysicalSynth.cpp:203`, `GrainSynth.cpp:143`, `Keys.cpp:219`, `Sampler.cpp:151`, and `Mosaic.cpp:336`, those 8 synths **do not have `if (idle_) return;`** at the top of `render()`!
  - Furthermore, `RackInstrument.cpp:64-101` and `DrumRack.cpp:46-106` unconditionally zero `scratch_`, call `c.instrument->render()`, and run every chain effect `d->process()` on every chain in every block, even when the chain's instrument and all its devices are `isIdle()`.
- **Decision**:
  1. Add `if (idle_)` fast-paths at the top of `render()` in all 8 synthesizers (`WavetableSynth`, `VoltSynth`, `Pentad`, `Consort`, `OperatorSynth`, `FluxSynth`, `BassSynth`, `Synth`), advancing any free-running LFO/delay phases in $O(1)$ arithmetic (following the precedent in `Keys.cpp:219-240`).
  2. In `RackInstrument::render()` and `DrumRack::render()`, check if `c.instrument->isIdle()` (and `!c.ctl->retrig` / `c.ctl->ampEnv <= 1e-5f` in `DrumRack`) and all non-bypassed chain devices report `d->isIdle()`. When the whole chain is idle, advance the instrument's idle clock via `c.instrument->render(scratch_, frames)` (which returns in $O(1)$), store `0.0f` to the chain meter, and skip zeroing `scratch_`, running chain devices, and summing zeros into `out`.
- **Rationale**: Keeping the `if (idle_)` fast-path inside each instrument's `render()` guarantees that free-running clocks (`lfoPhase1_`, `chorusPh_`, `Keys::lfo_`, `RhythmMachine` step clock) stay in phase while sleeping, and ensures that `playEvents()` automatically wakes the instrument at the exact sample offset `nOff` / `xOff` when a mid-block note or expression event arrives.
- **Alternatives considered**: Skipping `inst.render()` inside `playEvents()` without calling into the instrument — rejected for synths with free-running clocks (`Keys`, `RhythmMachine`, `WavetableSynth`) because their LFO/sequencer phases would freeze during silent bars.

---

## R2: How to optimize `WavetableSynth` (`oscStack`, `readMorph`, and `chorus`) bit-identically?

- **Finding**:
  1. `readMorph` (`WavetableSynth.h:282-290`) computes:
     ```cpp
     const double x = phase * kTableSize;
     int i0 = (int)x & (kTableSize - 1);
     int i1 = (i0 + 1) & (kTableSize - 1);
     const float xf = (float)(x - std::floor(x));
     ```
     Because `phase` in `oscStack` and `warpRead` is always in `[0.0, 1.0)`, `x` is always in `[0.0, 2048.0)`. For any non-negative `x < 2048.0`, `std::floor(x)` is mathematically identical to `static_cast<double>(static_cast<int>(x))`. Eliminating `std::floor(x)` removes 84 function/instruction calls per sample in a 6-voice Super Saw chord.
  2. In `oscStack` (`WavetableSynth.cpp:681-703`), `warpRead(mode, ph[u], warpAmt)` and `if (mode == 4) s = foldOut(s, warpAmt);` are evaluated inside the `for (int u = 0; u < uniN; ++u)` loop, even though `mode` is invariant across all unison voices. When `mode == 0` (Off — the default and Super Saw mode), `warpRead` is a no-op identity function. Splitting `mode == 0` into a dedicated tight loop and hoisting `1.0 / d` / `1.0 / (1.0 - d)` for `mode == 2` (Bend) and `mode == 3` (PWM) avoids per-unison-voice divisions and branches.
  3. In `WavetableSynth::chorus` (`WavetableSynth.cpp:785-821`), `chorusBuf_` has an arbitrary non-power-of-two size `(size_t)(sampleRate_ * 0.05) + 2`, requiring `while (rp < 0) rp += sz;` and `int i1 = (i0 + 1) % sz;`. Moreover, `tap(chorusPh_ + (double)k / voices)` calls `std::sin` `voices` times per sample (`voices` $\in \{1, 2, 4\}$).
     Notice that for `voices` $\in \{1, 2, 4\}$:
     - $k = 0$: $\sin(2\pi \phi) = s$
     - $k = 1$ (when `voices == 2`): $\sin(2\pi (\phi + 0.5)) = -\sin(2\pi \phi) = -s$
     - $k = 1, 2, 3$ (when `voices == 4`):
       - $k = 0$: $\sin(2\pi \phi) = s$
       - $k = 1$: $\sin(2\pi (\phi + 0.25)) = \cos(2\pi \phi) = c$
       - $k = 2$: $\sin(2\pi (\phi + 0.50)) = -s$
       - $k = 3$: $\sin(2\pi (\phi + 0.75)) = -c$
     Using trig identities reduces 2 or 4 calls to `std::sin` down to a single `std::sin` (and `std::cos` only when `voices == 4`), and rounding `chorusBuf_` up to a power of two replaces `% sz` and `while (rp < 0)` with `& mask`!
- **Decision**: Apply all three optimizations in `WavetableSynth.h`/`.cpp` and verify with `AuroraNullTests` (`< -120 dB`).
- **Rationale**: Zero approximation error; mathematical identity with dramatic reduction in transcendental and division instructions.

---

## R3: How to vectorize modal resonator banks in `PhysicalSynth` and `Keys`?

- **Finding**:
  - `PhysicalSynth::Bank` (`PhysicalSynth.h`) has `kModes = 16` modes in `double`:
    `y = b0[m] * x + a1[m] * y1[m] - a2[m] * y2[m]; y2[m] = y1[m]; y1[m] = y; acc += y;`
  - `Keys::Voice` (`Keys.h`) has `nModes <= kMaxModes` (`16`) modes in `float`:
    `y = a1[m] * y1[m] - a2[m] * y2[m] + b[m] * exc; y2[m] = y1[m]; y1[m] = y; x += y;`
  - In both instruments, all 16 modes are completely independent within a sample!
- **Decision**:
  - In `Keys`, align the arrays (`alignas(16) float a1[kMaxModes], a2[kMaxModes], b[kMaxModes], y1[kMaxModes], y2[kMaxModes]`) and pad `m` from `nModes` to `kMaxModes` with zeros (already done in `strike()`). Process 4 modes per iteration using `float32x4_t` (NEON) / `__m128` (SSE) in 4 unrolled vector steps.
  - In `PhysicalSynth`, process modes in 2-wide `double` vectors (`float64x2_t` on ARM64 NEON / `__m128d` on x86 SSE2), which preserves **exact double-precision arithmetic** (bit-identical `< -120 dB`!).
- **Rationale**: Using `float64x2_t` / `__m128d` in `PhysicalSynth` and `float32x4_t` / `__m128` in `Keys` matches each synth's native precision, avoiding any Q/decay precision loss in high-Q modal resonators while cutting loop iterations by $2\times$ to $4\times$.

---

## R4 (amended 2026-10-22 — paired SVF REVERTED after measurement)

- **Finding**: A paired 2-channel `svfStereo`/`filterPairStereo` (mid+side / L+R through
  shared coefficients in one 2-wide `double` step, per-lane arithmetic identical to
  `svf()`) was implemented for `WavetableSynth` and `VoltSynth` and then measured.
  An isolated microbenchmark (`/tmp/svf_ubench.cpp`, same `-O3` flags) shows the paired
  step at **1.2–2.1× the cost of two scalar `svf` calls on ARM64 (Apple Silicon)** —
  both with lane gather/scatter (`vset_lane`/`vget_lane`) and with lane-interleaved
  contiguous state (`vld1q`/`vst1q` only). Root cause: the kernel is latency-bound on
  the `ic1`/`ic2` feedback chain, and clang contracts the scalar op sequence into
  hardware FMA (default `FP_CONTRACT`), so scalar does fewer real operations while the
  intrinsics spell out separate mul+add; on top of that the vector form does not
  shorten the dependency chain. The engine-scale effect matched: `aurora-supersaw-2x`
  (stereo unison, the paired path) regressed ~18.6 → ~19.6 ms/s.
- **Decision**: REVERTED — both synths keep the scalar `svf`/`filterPair` traversals.
  The 2-wide `double` SIMD in `PhysicalSynth` modal banks (T014, measured −16% on
  `instrument-02`) and the 4-wide `float` SIMD in `Keys` (T015, neutral on ARM64,
  fewer instructions for the perf gate) are kept. Revisit paired-SVF only with
  x86-side measurements showing a win.
- **Alternatives considered**: lane-interleaved state relayout (`fst[filter][stage][ic][lane]`)
  to drop gather/scatter — rejected after the contiguous-layout ubench still measured
  1.08–1.66× slower than scalar on ARM64.

---

## R5: Why were `AudioBufferUtils.h` SIMD routines and proactive `rtParallel_` not active in `Engine_Render.cpp`?

- **Finding**:
  1. `Engine_Render.cpp:1801-1820` runs a scalar loop over `numFrames` for master volume scaling, `std::isfinite`, `std::clamp`, and peak/RMS/stereo-correlation accumulation, even though `AudioBufferUtils.h:148` provides `processMasterBus()` which does the exact same operations in a single SIMD pass.
  2. `Engine_Render.cpp:2332-2341`, `2436-2444`, and `2504-2514` run scalar loops to compute `pkL, pkR, sqL, sqR, sLR` right after calling `simd_audio::scaleStereo` / `addStereoScaled`, reading the buffer a second time in scalar code. When `!w.tapRec`, `scaleStereoMeter` / `addStereoScaledMeter` / `addStereoScaledMeterMap` from `AudioBufferUtils.h` fuse scaling and metering into a single SIMD pass.
  3. `Convolution.cpp:16-24` and `Chamber.cpp:274-282` run a scalar loop for `cmac` (complex multiply-accumulate over `B` frequency bins). Vectorizing `cmac` with NEON (`vmlaq_f32` / `vmlsq_f32`) and SSE (`_mm_add_ps` / `_mm_sub_ps` / `_mm_mul_ps`) accelerates partitioned FFT convolution by ~3×.
  4. In `Engine_Render.cpp:2251-2254`, `rtParallel_` only turns on when `leafWorkNs_ > periodNs` (100% of the buffer period). Lowering the activation threshold to `kRtEngageLoad = 0.65` (`0.65 * periodNs`) when `m >= 2` and `frames >= 128` wakes helper threads *before* an overload happens, while keeping 1-track or low-CPU sessions on the audio thread alone with zero thread-wake overhead.
- **Decision**: Wire `AudioBufferUtils.h` into `Engine_Render.cpp`, vectorize `cmac` in `Convolution.cpp` and `Chamber.cpp`, and add the `0.65 * periodNs` engagement threshold (gated on `m >= 2 && frames >= 128`).
