using Xunit;

// Native launch-profile tests temporarily set process-wide environment variables.
// Serial execution keeps one isolated profile from leaking into another test.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
