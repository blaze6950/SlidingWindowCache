# Actors — Shared Pattern

This document describes the **actor pattern** used across all cache implementations in this solution. Concrete actor catalogs for each implementation live in their respective docs.

---

## What Is an Actor?

In this codebase, an **actor** is a component with:

1. A clearly defined **execution context** (which thread/loop it runs on)
2. A set of **exclusive responsibilities** (what it does and does not do)
3. An explicit **mutation authority** (whether it may write shared cache state)
4. **Invariant ownership** (which formal invariants it is responsible for upholding)

Actors communicate via method calls (synchronous signals) or shared state reads. No message queues or actor frameworks are used — the pattern is conceptual.

---

## Universal Mutation Rule

Across all cache implementations, a single actor (the **Rebalance Execution** actor) holds exclusive write authority over shared cache state. All other actors are read-only with respect to that state.

This universal rule eliminates the need for locks on the read path and is enforced by internal visibility modifiers — not by runtime checks.

---

## Shared Actor Roles

Every cache implementation in this solution has the following logical actor roles:

| Role                       | Execution Context         | Mutation Authority         |
|----------------------------|---------------------------|----------------------------|
| **User Path**              | User / caller thread      | None (read-only)           |
| **Background Coordinator** | Dedicated background loop | None (coordination only)   |
| **Rebalance Execution**    | ThreadPool / background   | Sole writer of cache state |

The exact components that fill these roles differ between implementations. See:
- `docs/sliding-window/actors.md` — SlidingWindow actor catalog and responsibilities
- `docs/visited-places/actors.md` — VisitedPlaces actor catalog and responsibilities

---

## Execution Context Notation

Throughout the component docs, execution contexts are annotated as:

- ⚡ **User Thread** — runs synchronously on the caller's thread
- 🔄 **Background Thread** — runs on a dedicated background loop
- 🏭 **ThreadPool** — runs as a scheduled task on the .NET ThreadPool

---

## See Also

- `docs/shared/architecture.md` — single-writer architecture rationale
- `docs/sliding-window/actors.md` — SlidingWindow-specific actor responsibilities
- `docs/visited-places/actors.md` — VisitedPlaces-specific actor responsibilities
