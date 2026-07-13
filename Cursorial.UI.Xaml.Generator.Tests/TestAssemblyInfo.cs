// Several tests save/restore the process-global XamlLoaderOptions.DefaultMetadataProvider (the discovery /
// dual-run tests read and reset it) — so the assembly runs serialized for determinism (no cross-class race
// on that global). The suite is small and fast.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
