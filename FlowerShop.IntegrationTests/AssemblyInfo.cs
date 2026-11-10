
using Xunit;

// Run integration tests sequentially to avoid
// concurrent transactions on the shared test database.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
