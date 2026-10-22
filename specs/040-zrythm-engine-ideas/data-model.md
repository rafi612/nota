# Data Model: 040-zrythm-engine-ideas

## 1. Pruned set (US1)

- **Storage**: per-block `bool grpAud[]`/`retAud[]` stack arrays (O(tracks +
  groups)), computed after the leaf loop, before the parallel dispatch.
  Per-block because mute/solo are live relaxed atomics (`Track.h:283-284`).
- **Predicate**: single `leafAudibleBase()` shared by the leaf guard and the
  prune marking — never duplicated logic.
- **Invariants**: `capturing || frozenActive || feeding || keying` ⇒ audible;
  a group that will process (audible subtree or non-idle devices) marks its
  parent audible (tails flow upward); skip additionally requires own devices
  idle + PDC passthrough (`CompensationDelay::isIdle`). `tapRec` sources keep
  the slow path.

## 2. Fast mixer path (US2)

- **Inputs per block**: `peak` (from meter pre-pass), `gain`, `pan`, `tapRec`,
  `cross` flag.
- **Gates**: clip iff `peak > 1.0`; multiply iff `gain != 1.0` (exact float
  compare, bit-identical skip); clear iff routing is not 1:1.
- **Output**: identical buffers + meters vs slow path (`< -120 dB` null).

## 3. Decimated meters (US3)

- **Factor**: `kMeterDecim = 4` (tunable in plan, fixed per build).
- **State**: smoothing filter state advanced by `frames` equivalently;
  peak-hold/decay constants unchanged.

## 4. Voice shard (US4)

- **Storage**: prepare-time scratch `numVoices * ch * maxBlock` + `activeIdx[]`.
- **Dispatch**: `voices >= kMinVoices && frames >= kMinFrames` (defaults 2/16,
  tuned in tasks); serial ordered sum; nested-dispatch guard falls back serial.

## 5. Port source cache (US5)

- **Storage**: message-thread `vector<pair<Src*, Config>>` per port;
  `Config{mult, enabled, routing}` POD.
- **RT view**: raw `float*` in/out arrays materialised at prepare; audio thread
  only iterates.

## 6. MIDI buffer + rebuild flags (US6)

- **Buffer**: flat byte store + index array, `reserve()` at prepare/scratch
  publish, `clear()` keeps capacity.
- **Rebuild**: `softPending` (latencies) / `hardPending` (build+prune);
  at most one hard rebuild per block.
