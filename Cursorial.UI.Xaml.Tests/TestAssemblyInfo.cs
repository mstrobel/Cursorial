// These loader tests instantiate real Cursorial.UI elements, so they drive the property engine, which
// is single-UI-thread by contract (CLAUDE.md / design-doc invariant 6 — the per-type PropertyEffects /
// resolved-effects caches and metadata tables are plain dictionaries with no synchronization). They also
// share the process-global XamlSchemaContext.Default (each fixture registers its assembly + fixture
// namespace into it). Running test collections in parallel therefore drives concurrent reads/writes of
// those single-threaded structures and corrupts them. The sibling Cursorial.UI.Tests assembly serializes
// for exactly this reason; mirror it here. The suite is small and fast, so the cost is negligible.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
