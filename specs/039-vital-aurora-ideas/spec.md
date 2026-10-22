# Feature Specification: Aurora Optimizations Inspired by Vital DSP

**Feature Branch**: `039-vital-aurora-ideas`

**Created**: 2026-10-22

**Status**: Draft

**Input**: User description: "W roocie dodałem ci kod syntezatora vital, przeanalizuj go, powiedz jakie optymalizacje można by wprowadzić na jego bazie do aurora, zaplanuj to"

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

## Licensing constraint *(binding)*

Vital (`/Users/rafal/Projects/daw/vital`, outside this repo) is **GPL-3.0-or-later**
(`LICENSE`: "either version 3 of the License, or (at your option) any later version").
Nota is AGPL-3.0-only. This feature copies **zero lines of Vital code** and adds
**zero dependencies** — it re-implements well-known techniques (complex-rotation
oscillators, lookup tables, integer phases, mip-mapped wavetables) clean-room in
Nota's own DSP style. Algorithms are not copyrightable expression; the
implementation below is written from the technique descriptions in `research.md`,
not transcribed from Vital sources.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Chorus and LFOs cost no transcendental calls per sample (Priority: P1) 🎯 MVP

A musician playing Aurora pads with chorus and LFO modulation (e.g. Glass Choir,
Aurora Sweep, Wobble Wave) gets lower CPU usage while existing projects render
bit-identically (`< -120 dB` residual).

**Why this priority**: Vital never evaluates `sin`/`cos` per sample for modulation:
its reverb chorus advances the LFO by one complex multiply per sample from a
per-block increment (`effects/reverb.cpp:223-226`), and its chorus LFO is evaluated
once per block per tap (`modules/chorus_module.cpp:92-126`). Aurora instead calls
`std::sin` + `std::cos` per sample in `chorus()` (after 038-T011) and `std::sin`
per LFO per sample in `render()` (`lfoValue`, sine shape). A rotating phasor —
`(s,c)` multiplied by a per-block `(cos δ, sin δ)` increment, re-anchored from the
phase each block — reproduces the same sine with ~1e-13 phase error (residual
≈ −300 dB) at 6 flops instead of ~50+ for two libm calls. Additionally, LFO
evaluation is skipped entirely when provably inaudible (zero depth, no matrix
route, no filter-LFO amount — exact zeros, bit-identical by construction, same
pattern as 038's `pitchStatic` hoist).

**Independent Test**: `nota_perf run aurora-supersaw-2x` / `instrument-05` before and
after; `AuroraNullTests` (`--aurora-null`, all 10 factory presets) asserting
`< -120 dB`; `DspSilenceBypassTests` sleep/wake still `maxdiff 0.0`.

**Acceptance Scenarios**:

1. **Given** `WavetableSynth::chorus` with 1, 2 or 4 voices, **When** rendering any
   block, **Then** at most one `sincos` (or one `sin` + one `cos`) is evaluated per
   block to seed the tap phasors, every sample advances them by complex rotation,
   and the null against the pre-change engine is `< -120 dB`.
2. **Given** LFO1/LFO2 with sine shape, **When** rendering a block, **Then** the sine
   value comes from a per-block re-anchored rotator (no per-sample `std::sin`), and
   when the LFO is provably inaudible (depth ≈ 0, no active matrix row sourcing it,
   both filter-LFO amounts ≈ 0) the value path is skipped with `lfo = 0`, null
   `< -120 dB`.
3. **Given** a sleeping Aurora (`idle_`), **When** blocks are skipped, **Then** the
   rotator states stay consistent with the phase advance already performed there
   (rotation equivalent of `frames * inc`), so wake-up needs no correction.

---

### User Story 2 - Filter coefficient updates avoid `tan` per voice (Priority: P1)

A musician playing fast polyphonic passages with resonant filter motion gets lower
CPU usage with unchanged sound.

**Why this priority**: Vital precomputes `tan` into a 2048-entry table with cubic
lookup (`lookups/lookup_table.h:24-55`, used by `DigitalSvf`/`SynthFilter`), because
coefficient updates need `tan` far more often than the table costs. Aurora calls
`std::tan` in `setSvf` per voice every 16 samples. A process-static 2048-entry
`tan` table over the used normalized-cutoff range with linear (or Catmull)
interpolation replaces it; the table is built once, read-only afterwards (AR-6
safe), and the residual is verified `< -120 dB` (fallback: document `< -90 dB` if
linear proves insufficient and Catmull is adopted).

**Independent Test**: `AuroraNullTests` `< -120 dB`; benchmark as in US1.

**Acceptance Scenarios**:

1. **Given** any Aurora preset with filter motion, **When** rendering, **Then** no
   `std::tan` executes on the audio thread (table lookup only) and the null is
   `< -120 dB` (or `< -90 dB` with Catmull, explicitly recorded).

---

### User Story 3 - Oscillator phases are branchless integers (Priority: P2)

A musician playing high polyphony with wide unison gets lower per-voice overhead
and more stable performance.

**Why this priority**: Vital accumulates phase in `uint32` (`synth_oscillator.cpp:28-36`:
`kPhaseMult=2^32`), so wrapping is free (integer overflow) and the table index is
a bit-shift, with no `if (ph >= 1.0)` branch per unison voice per sample. Aurora
keeps `double ph[7]` per oscillator per voice with a conditional wrap per unison
voice per sample, plus double→int conversion in `readMorph`. A `uint32` phase
(2^-32 resolution, far below audibility) removes both; the residual vs double
phase is verified `< -90 dB` (phase quantization, not bit-identical by design).

**Independent Test**: `nota_perf` on `aurora-supersaw-2x` / `instrument-05`;
`AuroraNullTests` with the US3 gate `< -90 dB`; determinism self-null still
−240 dBFS.

**Acceptance Scenarios**:

1. **Given** any factory preset, **When** rendering, **Then** unison phase advance
   performs no branch and no double→int conversion per voice per sample, and the
   null against the pre-change engine is `< -90 dB`.

---

### User Story 4 - Wavetables are band-limited per pitch (Priority: P2, sound-improving)

A musician playing high notes on bright wavetables hears less aliasing, and the
engine touches less memory per read.

**Why this priority**: Vital renders each unison voice from a mip level chosen by
its phase increment (`last_harmonic = 2048 * exp2(-bin_shift)`,
`synth_oscillator.cpp:790-791`; spectral buffers refreshed only at ~7 ms fade
boundaries, never per sample). Aurora always reads the full 2048-point table, so
high notes alias *and* every read pulls full-size cache lines. Per-voice mip
selection (e.g. 2048/1024/512/256 tables built once at construction) fixes both.
This is a deliberate, audible improvement (less aliasing), so it is gated by an
aliasing measurement + listening, **not** by a null test.

**Independent Test**: aliasing-meter render (high sine-sweep note, harmonic vs
inharmonic energy ratio) before/after; `nota_perf`; full smoke test green.

**Acceptance Scenarios**:

1. **Given** a bright wavetable played 2+ octaves above middle C, **When** rendered,
   **Then** measured aliasing energy drops by ≥ 10 dB vs the full-table read while
   `aurora-supersaw-2x` wall time does not regress.
2. **Given** any factory preset in the normal playing range, **When** rendered,
   **Then** the change is inaudible in a blind A/B at normal listening levels
   (reviewer sign-off recorded in the PR).

---

### User Story 5 - Unison batching spike (Priority: P3, investigational)

A performance spike measures whether processing 4 unison voices (or 4 samples) per
SIMD step beats Aurora's scalar voice loop on the project's reference machines,
*before* committing to the complexity.

**Why this priority**: Vital batches 4 lanes (`poly_float`, `kSize=4`) across
voices×stereo through oscillators, filters and effects. But 038-T016/T017 proved
on ARM64 that pairing can be **slower** than scalar (latency-bound feedback chain
+ compiler FMA contraction of the scalar form). Any batching here (4-wide unison
reads in `oscStack`, or 4-sample time-batch per voice) must show a measured win on
both ARM64 and x86_64 before adoption — especially as NEON has no gather and
loads must be combined explicitly.

**Independent Test**: isolated microbenchmark (`/tmp`-style, same `-O3` flags as
the engine) + `nota_perf` A/B on both architectures if adopted.

**Acceptance Scenarios**:

1. **Given** the spike microbenchmark, **When** run on ARM64, **Then** it reports
   scalar-vs-batched ratio; adoption requires ≥ 1.2× win, otherwise the idea is
   recorded as rejected in `research.md` (as with 038-R4).

---

### Edge Cases

- **Rotator drift on very long notes**: phasors re-anchor from the true phase at
  every block start, bounding error to one block (~1e-13), so hour-long drones
  cannot drift.
- **S&H / triangle / square LFO shapes**: rotation applies to the sine shape only;
  other shapes keep their exact paths, and wrap detection still uses `lfoPhase_`.
- **Sleep/wake with rotators**: the T004 idle path advances phases by
  `frames * inc`; rotators advance by the same angle (`frames * δ`), so no
  wake-up correction is needed.
- **Mip switching clicks**: mip level is chosen per voice per block (not per
  sample), and adjacent levels differ only above the cutoff — no discontinuity in
  the audible band.
- **No Vital code in Nota**: every implementation is written clean-room from the
  technique descriptions; `LICENSES/third-party.md` unchanged.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: `WavetableSynth::chorus` MUST evaluate at most one `sincos` (or one
  `sin` + one `cos`) per block; per-sample tap LFO motion MUST come from complex
  rotation re-anchored at block start (null `< -120 dB`).
- **FR-002**: LFO1/LFO2 sine evaluation MUST use a per-block re-anchored rotator
  (no per-sample `std::sin`), and MUST be skipped with exact `0.0` when depth,
  matrix routes and filter-LFO amounts are all ≈ 0 (bit-identical skip).
- **FR-003**: `WavetableSynth::setSvf` MUST NOT call `std::tan` on the audio
  thread (process-static lookup table; null `< -120 dB`, fallback documented at
  `< -90 dB` with Catmull).
- **FR-004**: `oscStack` unison phases MUST advance without per-voice branches
  (`uint32` accumulators; null `< -90 dB`).
- **FR-005**: High-pitch voices MUST read from band-limited mip levels selected
  per voice per block (aliasing −10 dB or better; no wall-time regression).
- **FR-006**: Any 4-wide batching of `oscStack` MUST first pass the US5 spike
  (≥ 1.2× measured win on ARM64) before adoption.
- **FR-007**: Zero lines of Vital code in this repo; zero new dependencies
  (techniques re-implemented clean-room).
- **FR-008**: All changes MUST strictly obey `AR-6` (no alloc/lock/I-O on the
  audio thread) and `AR-7` (pure C ABI untouched).

### Key Entities

- **Phasor rotator**: per-block `(cos δ, sin δ)` increment + `(s, c)` state per
  modulated LFO (chorus taps, LFO1/LFO2 sine); re-anchored from the true phase at
  block start; error bounded to ~1e-13 per block.
- **Tan lookup table**: process-static 2048-entry table over the normalized
  cutoff range used by `setSvf`, built once on first use (UI thread), read-only
  on the audio thread.
- **Mip wavetable set**: per bank/frame tables at 2048/1024/512/256 points with
  per-voice, per-block level selection from the phase increment.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: `aurora-supersaw-2x` wall time improves a further **≥ 5%** on top of
  the 037 baseline, with `AuroraNullTests` `< -120 dB` for US1+US2 changes.
- **SC-002**: All factory Aurora presets keep `DspSilenceBypassTests`
  sleep/wake `maxdiff 0.0` and the full `./scripts/build.sh` gate green.
- **SC-003**: Aliasing energy on high bright notes drops **≥ 10 dB** (US4) with no
  `nota_perf` regression on the reference scenarios.
- **SC-004**: Zero audio-thread allocations/locks and zero new dependencies
  (verified by inspection + `LICENSES/third-party.md` unchanged).

## Assumptions

- ARM64 (`NOTA_SIMD_NEON`) is the reference dev machine; x86_64 (`NOTA_SIMD_SSE`)
  behavior is validated by CI + the perf gate, with scalar fallbacks everywhere.
- `std::sincos` availability is not assumed (MSVC): seed with one `std::sin` +
  one `std::cos` per block where needed.
- Chorus LFO rate ≤ 6 Hz and block ≤ 512 frames keep rotation error ~1e-13.

## Nota Compliance Checklist *(review before implementation; re-check in tasks.md)*

- [x] Every new source file will carry the AGPL-3.0-only SPDX header (two lines, see CONTRIBUTING.md)
- [x] No new dependency — or: license checked, AGPL-compatible, `LICENSES/third-party.md` row planned (no new dependency; no Vital code copied — see Licensing constraint)
- [x] Audio-thread impact considered: no alloc / lock / I/O on the audio thread (AR-6), command queue for data in (AR-4), immutable snapshots for structural edits (AR-5)
- [x] Managed/native crossings go through the pure C ABI in `nota_engine.h` only (AR-7); no JUCE outside `pluginhost/`
- [x] Smoke-test impact assessed: engine behaviour change → extend `tests/Nota.SmokeTest`
- [x] User-visible change → `CHANGELOG.md` `[Unreleased]` entry planned (and FEATURES.md / user docs via the nota-features / nota-docs skills when working with an agent that has them)
- [x] UI change → follows `DESIGN.md` (Studio design language) (N/A — engine-only)
