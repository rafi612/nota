# Feature Specification: Engine Optimizations Inspired by Zrythm DSP

**Feature Branch**: `040-zrythm-engine-ideas`

**Created**: 2026-10-22

**Status**: Draft

**Input**: User description: "Przeanalizuj silnik Zrythm i powiedz jakie optymalizacje można by wprowadzić w Nota; co możemy przenieść by cały silnik był szybszy? Napisz spec do tego."

## Nota Context

**Feature area**:

- [x] Engine (C++, `src/native/nota.engine`) — real-time audio rules apply, see `ARCHITECTURE.md` AR table
- [ ] Managed app (C#, `src/managed/*`)
- [ ] UI (Avalonia)
- [ ] Plugin hosting (`pluginhost/`)
- [ ] Build / packaging / CI
- [ ] Docs only

**Nota docs to keep in mind**:
[`ARCHITECTURE.md`](../../ARCHITECTURE.md) (AR rules),
[`CONTRIBUTING.md`](../../CONTRIBUTING.md) (licensing, dependency policy, PR
expectations), [`BUILD.md`](../../BUILD.md), [`FEATURES.md`](../../FEATURES.md).

Reference-only: `/Users/rafal/Projects/daw/zrythm/src/dsp/` (graph, ports,
fader, meters, voice manager). Zero code copied into Nota (Zrythm files carry
`LicenseRef-ZrythmLicense` headers); clean-room re-implementation of techniques
only, same rule as 039 vs Vital.

## User Scenarios & Testing

### User Story 1 - Muted/soloed/unreachable tracks cost nothing (Priority: P1) 🎯 MVP

A producer with a 32-track arrangement using solo/mute, monitor inputs, and
sidechain taps presses Play. Tracks that cannot reach the master bus (muted,
solo-gated, monitor-only feeds already handled) and group/return buses with no
audible contributor are pruned before the DSP passes, instead of iterating them
every block.

**Why this priority**: `Engine_Render.cpp:2079-2083` already skips fully
inaudible leaves per block, but groups/returns and tap-only paths are still
visited. Zrythm does this once per graph build (`graph_pruner.cpp:9-69`,
`graph_dispatcher.cpp:185-197`: backward DFS from terminals, `erase_if`).
Cheapest whole-engine win for large sessions; zero sonic change by construction
(pruned nodes contribute silence by the existing audibility predicate).

**Independent Test**: `nota_perf run sparse-32`, `mix-32`, plus a new
solo-one-of-32 scenario; `RenderPoolTests`; full smoke test green.

**Acceptance Scenarios**:

1. **Given** a session where only 1 of 32 leaves is audible, **When** rendering
   a block, **Then** pruned groups/returns/leaves execute no DSP and the mix
   nulls `< -120 dB` against the unpruned engine.
2. **Given** solo/mute/monitor changes, **When** the graph rebuilds, **Then**
   the pruned set is recomputed from terminals before Pass 1 and sidechain
   taps (`keying`/`feeding` in `Engine_Render.cpp:2072-2087`) still render.

---

### User Story 2 - Mixer and master bus skip redundant work (Priority: P1)

A producer playing back `mix-16`/`mix-32` gets lower mixer overhead: pan/gain
computed once per block, hard-clip executed only when needed, unity paths skip
multiplication.

**Why this priority**: `AudioBufferUtils.h:148-206` (`processMasterBus`) always
runs gain + NaN-squash + clip + meter in one pass. Zrythm limits only when
`magnitude > 2` (`audio_port.cpp:253-273`, comment: limiting ~50% of port CPU),
computes pan once per block (`fader.cpp:394-431`), and skips multiply on unity
(`cv_port.cpp:53-87`, `audio_bus_channel_routing.cpp:48-76`: 1:1 `copyFrom`
without `clear`). Direct port to `PanStage` (`Engine_Render.cpp:44-71`) and
leaf/group/return accumulate paths.

**Independent Test**: `nota_perf run mix-16`, `mix-32`, `sparse-32`; peak/RMS
meters verified against scalar reference in smoke test.

**Acceptance Scenarios**:

1. **Given** a block with peak `<= 1.0`, **When** the master bus runs, **Then**
   the clip sub-pass is skipped and output nulls `< -120 dB` vs always-clip.
2. **Given** a static-pan, unity-gain, `!tapRec` leaf/group/return, **When**
   accumulating, **Then** no per-sample multiply/branch runs and meters match
   the scalar path exactly.

---

### User Story 3 - Decimated peak/K meters (Priority: P2)

Meters, fall smoothing, and stereo correlation cost a fraction of today by
evaluating the smoothing filter every 4th sample with a x4-unrolled max loop
(Zrythm `kmeter_dsp.cpp:38-96`, `peak_dsp.cpp:50-103`, incl. `+1e-20f`
anti-denormal bias; Nota already has `ScopedNoDenormals`, keep it).

**Why this priority**: `StereoMeterAccumulator` (`SimdAudio.h`) runs full-rate
on every leaf/group/return/master. Decimation is inaudible for indications and
removes a fixed per-sample tax from every mix scenario.

**Independent Test**: `nota_perf run mix-16`; meter-accuracy test with slow
attacks/falls stays within existing smoke-test tolerances.

**Acceptance Scenarios**:

1. **Given** any mix scenario, **When** meters render, **Then** displayed
   peak/RMS/fall behaviour matches the full-rate reference within the
   smoke-test tolerance.
2. **Given** denormal-prone near-silence, **When** meters run, **Then** no
   denormal slowdown (bias or FTZ path, never `isfinite` on audio thread).

---

### User Story 4 - Voice-level parallelism inside heavy synths (Priority: P2)

Dense Aurora/Physical chords use idle cores inside a single track: voices
render in parallel above a threshold, summed serially in voice order
(bit-identical modulo FMA) — Zrythm `poly_voice_manager.h:128-144`,
`.cpp:179-259` (`kMinParallelVoices=2`, `kMinParallelBlockSamples=16`,
preallocated scratch, serial fallback).

**Why this priority**: Nota parallelises only across leaves
(`Engine.h:1781-1807`, `Engine_Render.cpp:2169-2252`); a single heavy track
(`aurora-supersaw-2x`, `instrument-05`) cannot scale. Gated strictly so small
sessions never regress (lesson from 037-R4 and 039-R4a/R6: adopt only on
measured win).

**Independent Test**: `nota_perf run aurora-supersaw-2x`, `instrument-05`,
`instrument-02`; `AuroraNullTests < -120 dB`; `nota_perf` shows no regression
on `silent-32`/`mix-16`.

**Acceptance Scenarios**:

1. **Given** `>= 2` active voices and `frames >= 16` (exact thresholds set in
   plan), **When** rendering, **Then** voices render via the pool and sum in
   voice order.
2. **Given** fewer voices or short frames, **When** rendering, **Then** the
   serial path runs (no pool dispatch on the audio thread beyond the existing
   leaf dispatch).

---

### User Story 5 - Port connection cache and subspan processing (Priority: P2)

Sends, returns, and device I/O iterate a prebuilt source list and process
in-place subspans (`offset`/`nframes`) instead of rebuilding maps and copying
full buffers per block — Zrythm `port.h:202-257` (`set_port_sources` once on
message thread), `processor_base.cpp:87-216` (materialised raw pointers,
`clear outs` before / `clear ins` after, clamp `offset+nframes <= max_block`).

**Why this priority**: Removes per-block map lookups and full-buffer copies on
the send/return/device paths. Medium gain, medium touch area.

**Independent Test**: `nota_perf run mix-32`, `effect-NN (x8)`; null
`< -120 dB` vs baseline.

**Acceptance Scenarios**:

1. **Given** a built graph, **When** a block renders, **Then** no connection
   lookup or allocation happens on the audio thread (all sources/curves
   materialised at prepare).
2. **Given** a sub-block split (loop seam, count-in), **When** processing,
   **Then** only `[offset, offset+nframes)` is touched and outs are cleared
   exactly once.

---

### User Story 6 - Packed MIDI buffers and coalesced graph rebuilds (Priority: P3)

MIDI across the engine uses a flat preallocated event buffer and graph rebuilds
coalesce soft (latencies only) vs hard (build+prune) — Zrythm
`midi_event_buffer.h:55-209` (packed `[u16 size][u32 time][bytes]`,
`reserve()` once, `clear` without dealloc) and
`graph_dispatcher.h:44-63` / `.cpp:135-233` (soft/hard coalescing).

**Why this priority**: `RenderScratch::blockMidi` (`Engine.h:1767-1776`) is
`vector<vector<MidiEv>>`; automation bursts rebuild the graph too often.
Small, safe, mostly message-thread work.

**Independent Test**: MIDI-heavy `nota_perf` scenario + automation-burst test;
no RT alloc (RTSan/`NOTA_RT_SAFE` clean).

**Acceptance Scenarios**:

1. **Given** dense MIDI input, **When** a block renders, **Then** no MIDI
   allocation happens on the audio thread and event order/timing is unchanged.
2. **Given** a burst of parameter/automation edits, **When** rebuilds queue,
   **Then** at most one hard rebuild runs per block with soft updates folded.

---

### Edge Cases

- **Tap-only audibility**: muted sidechain sources (`keying`/`feeding`) must
  survive pruning (US1) — prune predicate reuses the exact audibility logic
  in `Engine_Render.cpp:2070-2087`, never a copy of it.
- **Frozen/capturing tracks**: `capturing`/`frozenActive` bypass pruning and
  the meter fast paths; capture buffers stay sample-exact.
- **`tapRec == true`**: fast paths (US2/US5) disabled wherever the current
  code takes the `tapRec` serial branch.
- **Non-finite safety without `isfinite`**: `-ffast-math` is NOT adopted
  (Zrythm `CMakeLists.txt:495-521` + `AGENTS.md:313` warning); NaN/Inf squash
  stays branchless/range-based, consistent with `processMasterBus` today.
- **No nested pool dispatch**: voice-level work (US4) never dispatches from a
  pool worker (Zrythm `is_executing_pool_task` pattern,
  `fork_join_executor.cpp:30-33`); nested case falls back to serial.

## Requirements

### Functional Requirements

- **FR-001**: Pruning (US1) MUST derive from the single audibility predicate
  in `Engine_Render.cpp`, evaluated per block (mute/solo are live atomics, a
  publish-time set would go stale), and keep
  `feeding`/`keying`/`capturing`/`frozenActive` tracks rendering.
- **FR-002**: Master bus (US2) MUST skip the gain multiply when `mv == 1.0f`
  exactly (squash+clip+meter always run) and skip zero-fills of pruned
  group/return buses (never read when pruned), nulling `< -120 dB` vs baseline
  and keeping `tapRec`/frozen behaviour unchanged.
- **FR-003**: Decimated meters (US3) MUST stay within smoke-test meter
  tolerances and MUST NOT use `isfinite`/NaN checks on the audio thread.
- **FR-004**: Voice parallelism (US4) MUST be gated
  (`>= N` voices, `>= M` frames, built-ins only, no shared writers — same
  exclusions as leaf `eligible`), sum in voice order, and fall back to serial
  otherwise.
- **FR-005**: Port cache (US5) MUST materialise all sources/pointers at
  prepare time; audio thread performs zero lookup/alloc/lock.
- **FR-006**: MIDI buffers (US6) MUST be preallocated with `clear`-without-free
  semantics; rebuild coalescing MUST NOT drop a hard rebuild.
- **FR-007**: All changes MUST obey AR-6 (no alloc/lock/I-O on audio thread),
  AR-5 (immutable snapshots), AR-7 (pure C ABI untouched), and add no new
  dependencies (`LICENSES/third-party.md` unchanged). No Zrythm source copied.

### Key Entities

- **Pruned graph**: per-build audible set derived from terminals (master,
  monitor outs, capture queues, active taps).
- **Fast mixer path**: block-hoisted gain/pan, conditional clip, unity skip.
- **Voice shard**: per-voice scratch + serial ordered sum.
- **Port source cache**: message-thread-built source list with raw pointers.

## Success Criteria

- **SC-001**: Prune scenarios improve with `< -120 dB` null (measured:
  `mix-16-g` −6% A/B, peaks identical); no `nota_perf` scenario regresses
  beyond the perf-gate (1% instr / 15% wall). (Original ≥ 15% target for
  `sparse-32`/solo assumed group-heavy sessions; the harness scenarios carry
  no groups/returns, so the measured claim is the grouped half-muted mix.
  `sparse-32` itself is unchanged by construction — its leaves were already
  skipped pre-040.)
- **SC-002**: `mix-16`/`mix-32` show no regression (measured ±0.1–0.5%,
  within noise) with identical peaks and green meter tests. (Original ≥ 5%
  target via US2+US3 not met: US2 unity-mul/fill-skip is ~neutral by
  construction — master is 1 buffer vs 16 leaf chains, nothing pruned in
  full mixes — and US3 was rejected on its 0.8% upper-bound probe. Kept as
  no-risk hygiene, not as a speedup.)
- **SC-003**: `aurora-supersaw-2x` single-track scales with voices (target ≥
  1.2x on `>= 4` voices, else US4 is rejected like 039-R4a and stays out).
  → REJECTED per gate (measured +4.6%), fully reverted, recorded in R4b.
- **SC-004**: `scripts/build.sh` green (native + managed + smoke 100%), incl.
  extended null/meter/prune tests; `CHANGELOG.md [Unreleased]` entry.
- **SC-005**: Zero audio-thread alloc/lock/I-O (RTSan/`NOTA_RT_SAFE` clean),
  C ABI diff empty, no new third-party dependency.

## Assumptions

- ARM64 NEON first, x86-64 SSE2 second; scalar fallback always present.
- `nota_perf` scenarios (`silent-32`, `sparse-32`, `mix-16/32`,
  `aurora-supersaw-2x`, `instrument-02/05`) are the measurement baseline;
  medians-of-3 on a cooled machine, interleaved A/B.
- US4 reuses the existing `AudioThreadPool`; a separate intra-node executor is
  introduced only if the plan proves same-pool dispatch unsafe.
- Thresholds (`kMinParallelVoices`, block-size floors, clip `> 2` gate,
  meter decimation factor) are set in plan with a revert rule on regression.

## Nota Compliance Checklist

- [x] Every new source file will carry the AGPL-3.0-only SPDX header (two lines)
- [x] No new dependency — `LICENSES/third-party.md` unchanged planned
- [x] Audio-thread impact considered: no alloc/lock/I-O (AR-6), immutable snapshots (AR-5)
- [x] Pure C ABI untouched (AR-7); no JUCE outside `pluginhost/`
- [x] Smoke-test impact assessed: prune/null/meter/voice-order tests planned
- [x] User-visible change → `CHANGELOG.md [Unreleased]` entry planned (engine perf, no UI/manual surface)
- [x] UI change → N/A (engine-only)
