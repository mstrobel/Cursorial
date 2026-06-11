// The property engine is single-UI-thread by contract (invariant 6); the precedence-matrix tests
// additionally share process-global property registrations and metadata caches across test classes,
// so the assembly runs serialized for determinism (the suite is small and fast).
[assembly: CollectionBehavior(DisableTestParallelization = true)]
