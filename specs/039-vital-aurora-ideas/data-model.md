# Data Model: Aurora Optimizations Inspired by Vital DSP

**Feature**: `039-vital-aurora-ideas` | **Date**: 2026-10-22

No persistent project format or C ABI struct changes. Internal real-time DSP state only.

---

## 1. Phasor rotator (US1: chorus taps, LFO1/LFO2 sine)

Per modulated LFO, per block:

- **Increment**: `(cd, sd) = (cos δ, sin δ)`, `δ = 2π · rate / sampleRate`
  (`rate` = chorus tap rate or LFO rate incl. sync).
- **State**: `(s, c)` seeded once per block from the true phase
  (`s = sin(2πφ)`, `c = cos(2πφ)`), then per sample
  `(s', c') = (s·cd − c·sd, s·cd + c·sd)` — 4 mults + 2 adds, no branches.
- **Error bound**: FP accumulation ~1e-16/sample → ~1e-13 per 512-frame block;
  re-anchoring each block prevents unbounded drift (hour-long drones safe).
- **Sleep interaction**: the 037 idle path advances phases by `frames · inc`;
  rotators advance by the same angle `frames · δ` — no wake-up correction.

---

## 2. Tan lookup table (US2: `setSvf`)

- **Storage**: process-static `float[2048]` over the normalized cutoff argument
  range consumed by `setSvf`, built once on first use (never on the audio
  thread), read-only afterwards.
- **Lookup**: linear interpolation first; Catmull fallback if the `< -120 dB`
  gate fails (then gate documented at `< -90 dB`).

---

## 3. Mip wavetable set (US4)

- **Storage**: per bank/frame tables at 2048/1024/512/256 points, built once at
  construction (read-only shared data, same pattern as existing `sharedTables`).
- **Selection**: per voice per block from the phase increment
  (`level = f(base · detune)`); adjacent levels differ only above the cutoff, so
  block-rate switching causes no audible-band discontinuity.

## 4. Integer unison phase (US3)

- **Storage**: `uint64_t ph[7]` per oscillator per voice, NOT `uint32_t`: a
  32-bit increment truncation would drift ~1e-5 cycles over a 2.5 s render,
  failing the −90 dB gate; 64-bit drift is ~1e-14 (measured nulls stay
  `< -120 dB`).
- **Addressing**: index = `ph >> 53` (top 11 bits), fraction =
  `((ph >> 32) & 0x1FFFFF) / 2^21` (same 2048-point table geometry: 11 bits).
- **Step**: `(uint64_t)(inc · detune · 2^64)` per unison voice per sample
  (double mult kept, truncation exact for the fixed-point grid).
- **Warped modes**: exact 2^-53 phase via `(double)(ph >> 11) · 2^-53`,
  unchanged double warp math, result converted back with
  `(uint64_t)(rp · 2^64)` (rp ∈ [0,1) guaranteed).
