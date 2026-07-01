// UITestHost drives the single-UI-thread framework spine (invariant 6) and shares process-global state
// (UIApplication.Current, the WindowManager/popup surfaces, property registrations), so — like
// Cursorial.UI.Tests — the assembly runs serialized: parallel test classes clobber each other's app state
// (a mouse-click's WindowManager hit-test routing to the wrong host), producing order-dependent failures.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
