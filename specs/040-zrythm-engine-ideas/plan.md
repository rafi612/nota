# Implementation Plan: Engine Optimizations Inspired by Zrythm DSP

**Branch**: `040-zrythm-engine-ideas` | **Date**: 2026-10-22 | **Spec**: [spec.md](spec.md)

**Input**: Feature specification from `/specs/040-zrythm-engine-ideas/spec.md`

## Summary

Whole-engine CPU reduction in 6 independently shippable slices, all clean-room
re-implementations of Zrythm techniques (zero Zrythm source copied):

1. **US1 graph pruning (P1/MVP)** — message-thread backward reachability from
   terminals; pruned groups/returns/leaves skip DSP. Predicate reuses the exact
   audibility logic in `Engine_Render.cpp:2070-2087`.
2. **US2 mixer fast paths (P1)** — block-hoisted pan/gain, conditional master
   clip (`peak > 1.0` gate), unity-gain multiply skip, 1:1 copy without clear.
3. **US3 decimated meters (P2)** — smoothing filter every 4th sample, x4
   unrolled peak loop, anti-denormal bias (FTZ path kept).
4. **US4 voice parallelism (P2, gated)** — intra-synth voice shards above
   `>= N voices, >= M frames`, serial ordered sum, serial fallback; same-pool
   dispatch only if proven deadlock-free, else separate small executor.
5. **US5 port cache + subspans (P2)** — message-thread source lists, raw
   pointers at prepare, `[offset, offset+nframes)` in-place processing.
6. **US6 packed MIDI + coalesced rebuilds (P3)** — flat preallocated event
   buffer, soft (latency-only) vs hard (build+prune) rebuild coalescing.

## Technical Context

**Language/Version**: C++20 (`src/native/nota.engine/`), C# / .NET 10 smoke test.

**Primary Dependencies**: None new. Existing `dsp/math/Simd.h`, `SimdAudio.h`,
`AudioBufferUtils.h`, `FastMath.h`, `AudioThreadPool.h`.

**Storage**: N/A (in-memory real-time DSP).

**Audio-Thread Impact**: AR-6 strict — all audio-thread paths stay alloc/lock/
I-O-free. Pruning, source-list builds, MIDI `reserve()`, scratch sizing happen
on message/prepare threads; audio thread iterates raw pointers and POD spans.
No `isfinite` on audio thread (keeps `-ffast-math`-independence).

**Testing**: `tests/perf/nota_perf` (`silent-32`, `sparse-32`, `mix-16/32`,
`aurora-supersaw-2x`, `instrument-02/05`, + new solo-1-of-32);
`tests/Nota.SmokeTest` (null `< -120 dB`, meter tolerances, prune tests);
`scripts/build.sh` green gate; perf-gate 1% instr / 15% wall.

**Target Platform**: macOS 13+ (arm64+x86_64), Linux (x86_64, aarch64),
Windows 10/11 (x64, arm64). ARM64 NEON first, SSE2 second, scalar fallback.

**Performance Goals**: SC-001 sparse/solo ≥ 15%; SC-002 mix ≥ 5%;
SC-003 single-track voice scaling ≥ 1.2x or US4 rejected; zero regressions
beyond perf-gate.

**Constraints**: Tier-1 bit-identical `< -120 dB`; meters within smoke
tolerance; C ABI diff empty; `LICENSES/third-party.md` unchanged.

**Scale/Scope**: ~6 engine files (`Engine_Render.cpp`, `Engine.h`,
`Graph.cpp/h`, `AudioBufferUtils.h`, `SimdAudio.h`, voice render paths) +
smoke-test extensions + `CHANGELOG.md`.

## Constitution Check

- **I. RT Safety (AR-6)**: PASS — audio thread: raw-pointer iteration, POD
  spans, preallocated scratch only.
- **II. Boundary (AR-7, JUCE)**: PASS — `nota_engine.h`, `pluginhost/` untouched.
- **III. Licensing**: PASS — SPDX headers kept; no Zrythm source copied
  (`LicenseRef-ZrythmLicense` stays out); no new deps.
- **IV. Verification**: PASS — null/meter/prune tests + `build.sh` + `nota_perf`.
- **V. One change per PR**: PASS — 6 stories independently shippable.
- **VI. Docs**: PASS — `CHANGELOG.md [Unreleased]`; no UI/manual surface.
- **VII. Spec-First**: PASS — spec/plan/research/data-model/contracts/quickstart/tasks.
- **VIII. No rule weakening**: PASS.

## Project Structure

```text
specs/040-zrythm-engine-ideas/
├── spec.md
├── plan.md
├── research.md
├── data-model.md
├── quickstart.md
├── contracts/
│   └── engine-opt-contracts.md
└── tasks.md
```

**Structure Decision**: Engine-only change; all code under
`src/native/nota.engine/src/` (`engine/`, `dsp/math/`, `devices/`,
`audio_io/`); tests under `tests/Nota.SmokeTest/` and `tests/perf/`.
Branch `040-zrythm-engine-ideas` off the 039 tip (needs 037+039 perf baseline),
PR target `develop`.

## Complexity Tracking

| Violation | Why Needed | Simpler Alternative Rejected Because |
|-----------|------------|-------------------------------------|
| — | — | — |
