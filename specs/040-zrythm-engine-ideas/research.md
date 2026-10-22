# Research: Zrythm techniques transferable to Nota

**Sources**: `/Users/rafal/Projects/daw/zrythm/src/dsp/` (read-only reference).
Nothing below is copied; file:line refs are provenance for the idea only.

## R1: Graph pruning to terminals — ADOPT (US1)

- Zrythm `graph_pruner.h:18-33`, `graph_pruner.cpp:9-69`: DFS back from
  terminals into `unordered_set`, `erase_if(!marked) + remove_feed/depend`,
  called in `graph_dispatcher.cpp:185-197` after `build_graph`.
- Nota gap: `Engine_Render.cpp:2079-2083` skips inaudible leaves per block but
  still visits prunable groups/returns/tap-only paths every block.
- Transfer: message-thread reachability using the ONE audibility predicate
  (`Engine_Render.cpp:2070-2087`: `audible/capturing/feeding/keying`); frozen/
  capture bypass pruning. Expected: large sparse/solo sessions ≥ 15%.

## R2: Conditional clip + hoisted fader — ADOPT (US2)

- Zrythm `audio_port.cpp:253-273`: hard limit only if `magnitude > 2`
  (comment: limiting ~50% of port CPU); `fader.cpp:377-431`: pan once/block,
  `SmoothedValue` target once, SIMD `mul_k2/abs_max`.
- Nota gap: `AudioBufferUtils.h:148-206` always runs gain+squash+clip+meter.
- Transfer: `peakAndSumSquares` first; clip sub-pass gated on `peak > 1.0`;
  pan/gain hoist in `PanStage` (`Engine_Render.cpp:44-71`); unity skip +
  1:1 copy-without-clear (`audio_bus_channel_routing.cpp:48-76`,
  `cv_port.cpp:53-87` analogues).

## R3: Decimated meters — PROBED, REJECTED (US3)

- Zrythm `kmeter_dsp.cpp:38-96`, `peak_dsp.cpp:50-103`: x4 unroll, smoothing
  filter every 4th sample, `+1e-20f/+1e-10f` anti-denormal bias. That decimates
  a ballistics FILTER, not peak detection.
- Nota's `StereoMeterAccumulator` (`SimdAudio.h`) computes exact peak / RMS /
  L·R reductions — decimating peak would under-read transients, and decimating
  RMS/correlation biases user-visible meters. No `isfinite` on RT regardless
  (Zrythm `-ffast-math` lesson, `CMakeLists.txt:495-521` + AGENTS.md:313).
- Probe (2026-10-22): no-op'd all leaf+return meter accumulation
  (`addStereoScaledMeterMap` → plain `addStereoScaled`, meters zeroed) on
  `mix-16`, cooled interleaved A/B: 35.62 vs 35.90 (**0.8%** upper bound for
  ALL leaf/return metering). Any exact-preserving subset (e.g. skipping the
  `vprod` lane where `dummySLR` is passed, `Engine_Render.cpp:2488`) is a
  fraction of that on group buffers only — immeasurable, and decimation would
  degrade a visible surface for ~0.3%.
- Decision: REJECTED. Meter accumulation stays exact and fused.

## R4: Voice parallelism with thresholds — GATED SPIKE (US4)

- Zrythm `poly_voice_manager.h:128-144`, `.cpp:179-259`: `kMinParallelVoices=2`,
  `kMinParallelBlockSamples=16`, prepare-time scratch, serial ordered sum,
  serial fallback; separate `ForkJoinExecutor` (`fork_join_executor.h:99-146`)
  injected via `ProcessBlockInfo` so graph workers never self-deadlock.
- Nota: single `AudioThreadPool.h:91-158` sized for leaves. Rule: same-pool
  dispatch only with `thread_local is_executing_pool_task` guard
  (`fork_join_executor.cpp:30-33` analogue) + measured ≥ 1.2x on
  `aurora-supersaw-2x`, else REJECT (precedent 037-R4, 039-R4a/R6).

## R4b: Voice-shard spike — IMPLEMENTED, MEASURED, REVERTED (2026-10-22)

- Built: tiled block render (512-sample tiles) with a precomputed per-tile LFO
  series (identical `frand`/rotator trajectories), 2 voice shards sharing
  read-only state, serial ordered sum; `ShardExec{pool,busy}` plumbing
  (`Instrument::setShardExec`, `Engine::shardPool_` with 1 helper, lock-free
  claim flag preserving the pool's single-publisher discipline — same-pool
  nesting is UNSAFE: shared `done_`/`doneBase_` lets an inner wait exit early
  on outer completions, hence a separate pool exactly like Zrythm).
- Sound: serial shards null **−137 dB** (FP summation order only); full revert
  nulls **−240 dB** (bit-identical).
- Perf: real 2-thread shards measured **+4.6% slower** on `aurora-supersaw-2x`
  in a clean window (tight spreads both arms). Mechanism understood:
  per-block shard work is ~45–90 us while a parked-worker wake on macOS costs
  tens of us — every 512-frame block pays a wake it cannot amortize; the
  pool's linger heuristic (tuned for block cadence) parks between tiles.
  Gate was ≥ 1.2x: missed by a mile.
- Serial-restructure side observation (−3.4% in one clean window) proved
  environment-sensitive under a contaminated machine (`dasd` at 99% CPU:
  baseline bimodal 15.6/16.8) and unattributable; scaffolding for a rejected
  idea does not ship (same standard as 037-R4, 039-R6).
- Decision: FULLY REVERTED (no dead plumbing left behind). A future attempt
  needs zero-wake dispatch or coarser shards with a different wake-up story,
  not a different loop shape.

## R5: Port source cache + subspans — ALREADY SATISFIED, no change (US5)

- Zrythm `port.h:202-257` (`set_port_sources` on message thread),
  `processor_base.cpp:87-216` (raw `live_in/live_out`, clamp
  `offset+nframes <= max_block`, clear-outs-once/clear-ins-after).
- Verified: Nota already does the equivalent — raw bus/device pointers
  (`leafBase`, `groupBus_`, `routeBus_`), no per-block map lookups anywhere;
  the only scan is `routeSlotForTrack` (`Engine.h:1902-1906`, linear over
  `kMaxRoutes=8` atomics, ~6 calls/leaf/block ≈ 1 µs total); sends are direct
  atomic-array reads (`Track.h:200-204`); buses cleared per block; sub-block
  splits (loop seam, count-in) already pass split `frames` down (no
  offset+nframes plumbing needed). Hoisting `routeSlotForTrack` into
  `LeafWork` would save ~1000 atomic loads/block — immeasurable, declined.
- Decision: NO CODE. The pattern matches; nothing to port.

## R6: Packed MIDI + soft/hard recalc — ALREADY SATISFIED, no change (US6)

- Zrythm `midi_event_buffer.h:55-209` (packed layout, `reserve()` once,
  swap-based sort/filter), `graph_dispatcher.h:44-63` (soft=latencies,
  hard=build+prune, re-entrant deferred).
- Verified: `RenderScratch::blockMidi/blockExpr` inner buffers are pre-sized
  (1024/`kMaxBlockExpr`) at publish and written via `buf.data()` + count —
  zero audio-thread allocation by construction (`Engine_Render.cpp:1232-1265`
  AR-6 comment). Packing would only touch control-path locality: immeasurable.
  Rebuild coalescing has no trigger in Nota's architecture: structural edits
  publish snapshots (already minimal), automation/params ride atomic lanes
  without rebuilds.
- Decision: NO CODE. Substance already present; no burst-rebuild path exists.

## R7: Deliberately NOT ported

- `float_ranges` on JUCE `FloatVectorOperations` — Nota `Simd.h` is better.
- Global `-ffast-math` — breaks `isfinite`; Nota stays range-based.
- Zrythm `panning.cpp:31-41` per-block `sqrt/cos` — worse than Nota `FastMath`.
- `ditherer.h:32-43` naive TODO — no win.
