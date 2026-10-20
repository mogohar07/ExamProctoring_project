namespace ExamProctoring.IntegrationTests;

// Every test class shares one ApiFactory, and so one ExamProctoring_Tests database. Classes in the same
// collection run one after another, so two factories never drop and recreate the database at the same time.
[CollectionDefinition(Name)]
public sealed class ApiCollection : ICollectionFixture<ApiFactory>
{
    public const string Name = "Api";
}
