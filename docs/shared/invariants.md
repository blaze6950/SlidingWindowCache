# Invariants — Shared

Invariants that apply across all cache implementations in this solution. These govern the shared infrastructure: activity tracking and disposal.

For implementation-specific invariants, see:
- `docs/sliding-window/invariants.md` — SlidingWindow invariant groups SWC.A–SWC.I
- `docs/visited-places/invariants.md` — VisitedPlaces invariant groups VPC.A–VPC.T

---

## Invariant Legend

- 🟢 **Behavioral** — Directly observable; covered by automated tests
- 🔵 **Architectural** — Enforced by code structure; not tested directly
- 🟡 **Conceptual** — Design-level guidance; not enforced at runtime

---

## S.R. Range Request Invariants

**S.R.1** 🟢 **[Behavioral]** **The requested range must be bounded (finite) on both ends.**

`GetDataAsync` rejects any `requestedRange` that is unbounded (i.e., extends to negative or positive infinity) by throwing `ArgumentException`. Both cache implementations enforce this at the public entry point, before any delegation to internal actors.

**Rationale:** Unbounded ranges have no finite span and cannot be fetched, stored, or served. Accepting them would propagate a nonsensical request into the data source and internal planning logic, producing undefined behavior. Validating eagerly at the entry point gives the caller an immediate, actionable error.

**Enforcement:** `SlidingWindowCache.GetDataAsync`, `VisitedPlacesCache.GetDataAsync`

---

## S.H. Activity Tracking Invariants

These invariants govern `AsyncActivityCounter` — the shared lock-free counter that enables `WaitForIdleAsync`.

**S.H.1** 🔵 **[Architectural]** **Activity counter is incremented before work is made visible to other threads.**

At every publication site, the counter increment happens before the visibility event:
- Before `semaphore.Release()` (intent signalling)
- Before channel write (`BoundedSerialWorkScheduler`)
- Before `lock (_chainLock)` task chain update (`UnboundedSerialWorkScheduler`)

**Rationale:** If the increment came after visibility, a concurrent `WaitForIdleAsync` caller could observe the work, see count = 0, and return before the increment — believing the system is idle when it is not. Increment-before-publish prevents this race.

---

**S.H.2** 🔵 **[Architectural]** **Activity counter is decremented in `finally` blocks.**

Every path that increments the counter (via `IncrementActivity`) has a corresponding `DecrementActivity()` in a `finally` block — unconditional cleanup regardless of success, failure, or cancellation.

**Rationale:** Ensures the counter remains balanced even when exceptions or cancellation interrupt normal flow. An unbalanced counter would leave `WaitForIdleAsync` permanently waiting.

---

**S.H.3** 🟡 **[Conceptual]** **`WaitForIdleAsync` has "was idle at some point" semantics, not "is idle now" semantics.**

`WaitForIdleAsync` completes when the activity counter **reached** zero — signalling that the system was idle at that moment. New activity may start immediately after the counter reaches zero, before the waiter returns from `await`.

**Formal specification:**
- `WaitForIdleAsync` captures the current `TaskCompletionSource` at the time of the call
- When the counter reaches zero, the TCS is signalled
- A new TCS may be created immediately by the next `IncrementActivity` call
- The waiter observes the old (now-completed) TCS and returns

**Implication for users:** After `await WaitForIdleAsync()` returns, the cache may already be processing a new request. Do not assume quiescence after the call.

**Implication for tests:** `WaitForIdleAsync` is sufficient for asserting that a specific rebalance cycle completed — but re-check state if strict quiescence is required.

---

**S.H.4** 🔵 **[Architectural]** **`AsyncActivityCounter` is fully lock-free.**

All operations use `Interlocked` for counter modifications and `Volatile` reads/writes for TCS publication. No locks, no blocking.

**Implementation:** `src/Intervals.NET.Caching/Infrastructure/Concurrency/AsyncActivityCounter.cs`

---

## S.J. Disposal Invariants

**S.J.1** 🔵 **[Architectural]** **Post-disposal guard on all public methods.**

After `DisposeAsync()` completes, all public method calls on the cache instance throw `ObjectDisposedException`. The disposal state is checked via `Volatile.Read` at the start of each public method.

---

**S.J.2** 🔵 **[Architectural]** **Disposal is idempotent.**

Multiple calls to `DisposeAsync()` are safe. Subsequent calls after the first are no-ops.

---

**S.J.3** 🔵 **[Architectural]** **Disposal cancels background operations cooperatively.**

On disposal, the loop cancellation token is cancelled. Background loops observe the cancellation and exit cleanly. Disposal does not forcibly terminate threads.

---

**S.J.4** 🟡 **[Conceptual]** **`WaitForIdleAsync` after disposal is not guaranteed to complete.**

After the background loop exits, the activity counter may remain non-zero (if a loop iteration was interrupted mid-flight). Callers should not call `WaitForIdleAsync` after disposal.

---

## See Also

- `docs/shared/architecture.md` — AsyncActivityCounter design rationale
- `docs/shared/components/infrastructure.md` — AsyncActivityCounter implementation details
- `docs/sliding-window/invariants.md` — SlidingWindow-specific invariant groups (SWC.A–SWC.I)
- `docs/visited-places/invariants.md` — VisitedPlaces-specific invariant groups (VPC.A–VPC.T)
