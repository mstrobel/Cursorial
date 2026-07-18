// The UI stack is single-UI-thread by contract (invariant 6) and shares process-global state
// (UIApplication.Current, property registrations, ThemeContributions); the shaping engine's tests
// additionally assert allocation contracts that demand a quiet process. The assembly runs serialized
// for determinism (the repo-wide convention for UI-adjacent test assemblies).
[assembly: CollectionBehavior(DisableTestParallelization = true)]
