// Disable xUnit parallel test execution for this assembly.
// The Aioi gateway supports up to 5 simultaneous TCP sessions.
// Running test classes sequentially prevents connection-refused errors
// when multiple test classes each open their own raw TCP connections.
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]
