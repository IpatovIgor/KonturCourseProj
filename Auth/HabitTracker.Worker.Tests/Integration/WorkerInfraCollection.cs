using Xunit;

namespace HabitTracker.Worker.Tests.Integration;

[CollectionDefinition("worker-infra")]
public sealed class WorkerInfraCollection : ICollectionFixture<WorkerInfrastructureFixture>;