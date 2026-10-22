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

## R3: Decimated meters — ADOPT (US3)

- Zrythm `kmeter_dsp.cpp:38-96`, `peak_dsp.cpp:50-103`: x4 unroll, smoothing
  filter every 4th sample, `+1e-20f/+1e-10f` anti-denormal bias.
- Nota keeps `ScopedNoDenormals`; add decimation to `StereoMeterAccumulator`
  (`SimdAudio.h`) with smoke-tolerance check. No `isfinite` on RT (Zrythm
  `-ffast-math` lesson, `CMakeLists.txt:495-521` + AGENTS.md:313).

## R4: Voice parallelism with thresholds — GATED SPIKE (US4)

- Zrythm `poly_voice_manager.h:128-144`, `.cpp:179-259`: `kMinParallelVoices=2`,
  `kMinParallelBlockSamples=16`, prepare-time scratch, serial ordered sum,
  serial fallback; separate `ForkJoinExecutor` (`fork_join_executor.h:99-146`)
  injected via `ProcessBlockInfo` so graph workers never self-deadlock.
- Nota: single `AudioThreadPool.h:91-158` sized for leaves. Rule: same-pool
  dispatch only with `thread_local is_executing_pool_task` guard
  (`fork_join_executor.cpp:30-33` analogue) + measured ≥ 1.2x on
  `aurora-supersaw-2x`, else REJECT (precedent 037-R4, 039-R4a/R6).

## R5: Port source cache + subspans — ADOPT (US5)

- Zrythm `port.h:202-257` (`set_port_sources` on message thread),
  `processor_base.cpp:87-216` (raw `live_in/live_out`, clamp
  `offset+nframes <= max_block`, clear-outs-once/clear-ins-after).
- Transfer to send/return/device paths; sub-block splits (loop seam,
  count-in) touch only `[offset, offset+nframes)`.

## R6: Packed MIDI + soft/hard recalc — ADOPT (US6)

- Zrythm `midi_event_buffer.h:55-209` (packed layout, `reserve()` once,
  swap-based sort/filter), `graph_dispatcher.h:44-63` (soft=latencies,
  hard=build+prune, re-entrant deferred).
- Transfer: flat buffer for `RenderScratch::blockMidi` (`Engine.h:1767-1776`);
  soft/hard flags around `inheritGraphLevel()` (`Graph.cpp:8-15`).

## R7: Deliberately NOT ported

- `float_ranges` on JUCE `FloatVectorOperations` — Nota `Simd.h` is better.
- Global `-ffast-math` — breaks `isfinite`; Nota stays range-based.
- Zrythm `panning.cpp:31-41` per-block `sqrt/cos` — worse than Nota `FastMath`.
- `ditherer.h:32-43` naive TODO — no win.
