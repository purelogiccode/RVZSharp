// The CLI tests temporarily set process-wide environment variables (for example
// RVZSHARP_NO_UPDATE_CHECK), so parallel collections would make them order-dependent.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
