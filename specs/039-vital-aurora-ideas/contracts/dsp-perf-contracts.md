# Contracts: Internal DSP & Benchmark Verification

**Feature**: `039-vital-aurora-ideas` | **Date**: 2026-10-22

The public C ABI (`include/nota/nota_engine.h`) is **unchanged** (AR-7). Zero lines
of Vital code enter this repo (GPL-3.0-or-later); all work is clean-room
re-implementation (see spec Licensing constraint).

---

## 1. Null-test sound preservation contract

- **Tier-1** (rotators US1, inaudible-LFO skip, tan LUT US2):
  offline render residual vs pre-change engine **`< -120 dB`**
  (bit-identical or FP-association equivalent; rotator error ~1e-13).
  Tan LUT fallback: `< -90 dB` with Catmull, explicitly recorded.
- **Tier-2** (integer phases US3): residual **`< -90 dB`**
  (phase quantization 2^-32, inaudible by construction).
- **Sound-improving** (mip tables US4): gated by aliasing measurement
  (≥ 10 dB reduction), **not** by null test.

## 2. `nota_perf` benchmark scenarios contract

Same scenarios as 037 (`silent-32`, `sparse-32`, `aurora-supersaw-2x`,
`instrument-05`, `instrument-06`, `mix-16`), plus before/after wall-time
comparison per user story. New work must not regress any scenario beyond noise
(±5% run-to-run on the reference machine; medians of 3 runs).

## 3. Spike acceptance contract (US5)

Adoption of 4-wide unison batching requires an isolated microbenchmark (same
`-O3` flags as the engine build) showing **≥ 1.2× win on ARM64**, plus a
`nota_perf` A/B with no scenario regressing. Otherwise the idea is recorded as
rejected in `research.md` (precedent: 037-R4 paired SVF).
