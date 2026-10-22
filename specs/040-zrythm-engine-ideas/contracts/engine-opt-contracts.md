# Contracts: 040-zrythm-engine-ideas (engine-opt)

## C ABI

- Diff of `include/nota/nota_engine.h`: empty (AR-7).

## Audio-thread (AR-6)

- No `new/malloc/free`, no mutex/futex, no I/O, no `isfinite` on RT paths in
  `Engine_Render.cpp`, `AudioBufferUtils.h`, `SimdAudio.h`, voice render,
  port accumulate, MIDI iterate.
- All tables/lists/scratch reserved on message/prepare threads.

## Sound preservation

- US1/US2/US4/US5: `< -120 dB` null vs pre-change baseline.
- US3 meters: within existing smoke-test tolerances (indications only).
- Prune/pack/rebuild changes: `maxdiff 0.0` on silence/soak tests.

## Perf gates

- `nota_perf` medians-of-3, cooled, interleaved A/B.
- Adopt US4 only on ≥ 1.2x; any scenario beyond 1% instr / 15% wall ⇒ revert.
