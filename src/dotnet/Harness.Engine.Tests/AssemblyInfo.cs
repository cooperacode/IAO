// StateStore and TaskRegistry share a fixed state file (.harness/state.json), so the
// tests need to run serially to avoid corrupting each other's state.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
