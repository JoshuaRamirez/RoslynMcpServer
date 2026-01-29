using Xunit;

namespace RoslynMcp.Core.Tests.Integration;

/// <summary>
/// Collection definition for integration tests.
/// Tests in the same collection run sequentially.
/// </summary>
[CollectionDefinition("Integration Tests", DisableParallelization = true)]
public class IntegrationTestCollection
{
}
