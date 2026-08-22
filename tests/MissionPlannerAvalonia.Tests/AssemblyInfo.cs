using Xunit;

// Mission Planner exposes process-wide vehicle state and Avalonia exposes one UI dispatcher.
// Running test classes in parallel can interleave those shared singletons and can make the
// headless runner attempt a nested Dispatcher.PushFrame. Individual concurrency tests still
// create their own parallel workloads explicitly.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
