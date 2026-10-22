# Research: Aurora Optimizations Inspired by Vital DSP

**Feature**: `039-vital-aurora-ideas` | **Date**: 2026-10-22

Vital source: `/Users/rafal/Projects/daw/vital/src` (GPL-3.0-or-later — **no code
copied**; techniques below are re-described in our own words and will be
re-implemented clean-room). Aurora references are
`src/native/nota.engine/src/devices/instruments/WavetableSynth.{h,cpp}` after
038/037 (idle fast-paths, hoisted warp, power-of-two chorus, paired-SVF revert).

---

## R1: Per-sample transcendental calls — Vital's answer is rotation, not polynomials

- **Vital facts** (verified in source):
  - Reverb chorus advances its LFO by one complex multiply per sample from a
    per-block increment — no per-sample `libm` (`effects/reverb.cpp:223-226`,
    `chorus_increment=cos/sin(phase_inc)` once per block).
  - Chorus LFO phase advances once per block per tap
    (`modules/chorus_module.cpp:92-126`); per-tap `utils::sin` runs per block, not
    per sample (line 116 is in the per-tap loop, outside the sample loop).
  - LFOs use a 2048-point table with Catmull lookup per sample
    (`modulators/synth_lfo.h:80-91`, `common/synth_constants.h:25-37`
    `kLfoDataResolution=2048`); envelopes run audio-rate *or* one step per block
    (`modulators/envelope.cpp:22-117` control-rate branch).
  - General `sin`/`tanh`/`exp2` exist as polynomials (`framework/futils.h:180-317`:
    `quickSin` = `x*(8-16|x|)` corrected, `quickTanh` rational, `exp2` 5th-order),
    but the per-sample modulation paths prefer tables/rotation.
- **Aurora today**: `chorus()` does 1× `std::sin` + (4 voices) 1× `std::cos` per
  sample (037-T011); `render()` does up to 2× `std::sin` per sample for LFO1/LFO2
  sine shape (`lfoValue`), plus S&H/tri/sqr branches. `setSvf` does `std::tan` per
  voice every 16 samples.
- **Decision**: adopt rotation, not polynomials:
  1. Chorus taps: one `sin`+`cos` per block to seed, per-sample complex rotation
     (6 flops vs ~50+ for two libm calls; error ~1e-13/block, gate `< -120 dB`).
  2. LFO sine: same rotator treatment, re-anchored per block; skip entirely when
     provably inaudible (bit-identical, extends the 037 `pitchStatic` pattern).
  3. `setSvf`: process-static 2048-entry `tan` table (Vital:
     `lookups/lookup_table.h:24-55`, `filters/digital_svf.cpp:32-35`), linear
     first, Catmull fallback with `< -90 dB` gate.
- **Rationale**: rotation preserves the exact sine (unlike polynomials, which
  Aurora already rejected twice at −44/−51 dB on pitch-critical paths), while
  matching Vital's per-sample cost profile.
- **Alternatives considered**: Vital-style `quickSin` polynomial — rejected for
  pitch-routed LFO (fails `< -120 dB` by design); block-constant chorus delays —
  rejected (linear-hold error up to ~6 samples at 6 Hz rate, fails null).

---

## R2: Integer phase accumulators (branchless wrap)

- **Vital facts**: phase is `uint32` (`synth_oscillator.cpp:28-36`,
  `kPhaseMult=2^32`); advance is one integer add, wrap is free overflow, table
  index is a shift (`>>21`), fraction from masked low bits (`205-216`).
- **Aurora today**: `double ph[7]` per oscillator per voice, `if (ph >= 1.0)
  ph -= 1.0` per unison voice per sample, plus double→int conversion in
  `readMorph`.
- **Decision**: `uint32` phase per unison voice (2^-32 resolution, inaudible);
  index = `ph >> 21`, fraction = `(ph & mask) / 2^21`. Gate `< -90 dB` (phase
  quantization changes rounding vs double; determinism self-null must stay
  −240 dBFS).
- **Rationale**: removes a hard-to-predict branch and float→int converts from the
  innermost loop; matches Vital's addressing exactly in structure (own code).

---

## R3: Mip-mapped band-limited wavetables (quality + cache)

- **Vital facts**: per-unison spectral mip chosen from the phase increment
  (`last_harmonic = 2048 * exp2(-bin_shift)`,
  `synth_oscillator.cpp:790-791`); spectral morph + inverse FFT happens only at
  ~7 ms fade boundaries (`setFourierWaveBuffers`, `1356-1428`), never per
  sample; per-sample cost stays one 4-pt + one linear interpolation.
- **Aurora today**: always reads the full 2048-point table → high notes alias,
  and every read touches full-size lines.
- **Decision**: build 2048/1024/512/256 mip levels once (construction +
  `setSampleRate`), select per voice per block from `base * detune` increment.
  Deliberate audible improvement (less aliasing): gate by aliasing measurement
  (≥ 10 dB reduction) + blind A/B sign-off, **not** by null test.
- **Rationale**: the only idea in this package that changes the sound on purpose;
  everything else is fenced by null gates.

---

## R4: 4-wide batching — spike first (lesson from 037-R4)

- **Vital facts**: `poly_float` is 4-wide across voices×stereo
  (`framework/poly_values.h:56-60,443-449`, `kSize=4` on SSE2/NEON, AVX2 stubbed);
  used in oscillators, every filter tick, delay lines and modulators.
- **Aurora lesson**: 037-T016/T017 paired 2-wide SVF measured **1.2–2.1× slower
  than scalar on ARM64** (latency-bound feedback chain + compiler FMA contraction
  of the scalar form) and was reverted. NEON has no gather, so 4-wide unison
  reads need explicit lane combines — the same overhead class that killed T016.
- **Decision**: US5 is a measurement spike (isolated ubench + `nota_perf` A/B on
  ARM64; x86 data from CI if promising). Adopt only on ≥ 1.2× measured win;
  otherwise record rejection here.

## R4a: Unison batching spike — REJECTED (2026-10-22)

- Isolated ubench (`/tmp/unison_ubench.cpp`, same `-O3`): scalar 7-voice stack
  vs 4-wide NEON with scalar-load gathers — **0.89× / 1.05×** (noise). The
  gather (8 scalar loads + 4 lane combines per 4 voices) costs everything the
  vector ALU saves, exactly the 037-T016 failure mode.
- **Decision**: REJECTED on ARM64. No batching work enters the engine.

---

## R5: What is deliberately NOT ported

- **FDN reverb** (`effects/reverb.h`: 16-voice FDN, Lagrange interpolation):
  different product sound — porting it would change Aurora's Room/Hall/Plate.
- **Oversampling** (`filters/upsampler/decimator`, `sound_engine.cpp:226-240`):
  costs CPU to *improve* quality; not an optimization for us.
- **Diode/ladder/formant models**: different filters, not faster versions of ours.
- **Voice-killer / mod-source disabling**: Aurora already covers the equivalent
  (`live` counter, `amN==0` skip, `pitchStatic`, US1 auto-sleep, V2 LFO skip).

## R6: Mip-mapped wavetables — implemented, measured, REVERTED

- **What was built**: full clean-room implementation — 2048/1024/512/256 tables
  with harmonics cut at 64/32/16/8 sharing the full table's peak normalization
  (kept partials bit-comparable across levels), per-call level ladder
  (`inc·18/34/66 ≥ detMax`), then block-uniform levels (min over active voices),
  then branchless base-table indexing; mip bases cached per instance (no
  per-call static guards).
- **Measurements** (`aurora-supersaw-2x`, ARM64, interleaved A/B/A/B on a cool
  machine, median-of-3):
  - per-call selection (mixed L0/L1 across voices): **17.55 vs 15.79 L0 (−11%)**;
    forced-uniform L1: 15.74 — i.e. mixing, not the L1 reads, costs.
  - block-uniform levels + cached bases: **16.4 vs 15.9 (−2.5–3%)**.
  - isolated `readMorph` const-vs-runtime ubench: 0.92× (not the culprit).
  - Conclusion: any per-call level select (branch or arithmetic) and any
    per-call table-set switch costs more than smaller tables save — Apple
    Silicon L1D/stream prefetchers already serve the 8 KB full tables; the win
    Vital gets comes from machines with smaller caches and from its different
    (per-voice, not per-sample) loop structure.
- **Decision**: REVERTED to the T008 code (verified: reverted tree measures
  15.62 = forced-L0 15.64, null still `< -120 dB` vs the pre-039 baseline).
  The aliasing benefit itself was never disputed (dropped partials are provably
  ultrasonic by the selection rule), only its price. A future attempt needs a
  zero-overhead selection design (e.g. per-voice persistent levels, no per-call
  work) — not per-call or per-block ladders.
