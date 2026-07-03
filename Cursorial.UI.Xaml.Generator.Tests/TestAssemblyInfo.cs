// Several tests load generated-provider assemblies whose [ModuleInitializer]s mutate the process-global
// XamlLoaderOptions.DefaultMetadataProvider, and the module-init / dual-run tests read it — so the assembly
// runs serialized for determinism (no cross-class race on that global). The suite is small and fast.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
