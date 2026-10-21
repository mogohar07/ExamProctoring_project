using ExamProctoring.Application.Accounts;

namespace ExamProctoring.UnitTests.Accounts;

// Instructor creation uses the same full-name and email rules as student sign-up (§2.2).
public sealed class CreateInstructorRequestValidatorTests
{
    private readonly CreateInstructorRequestValidator _validator = new();

    [Theory]
    [InlineData("Dr. Mona Hassan", "mona@university.edu")]
    [InlineData("محمد عبد الله", "محمد@مثال.مصر")]
    [InlineData("O'Brien-Smith", "o'brien@university.edu")]
    public void ValidRequest_HasNoErrors(string fullName, string email) =>
        Assert.True(_validator.Validate(new CreateInstructorRequest(fullName, email)).IsValid);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Mona\u0007Hassan")]
    public void InvalidFullName_IsRejected(string? fullName) =>
        AssertOnlyFieldFails(new CreateInstructorRequest(fullName, "mona@university.edu"), nameof(CreateInstructorRequest.FullName));

    [Fact]
    public void FullName_IsLimitedTo150CharactersAfterTrimming()
    {
        Assert.True(_validator.Validate(new CreateInstructorRequest("  " + new string('a', 150) + "  ", "mona@university.edu")).IsValid);
        AssertOnlyFieldFails(new CreateInstructorRequest(new string('a', 151), "mona@university.edu"), nameof(CreateInstructorRequest.FullName));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-an-email")]
    [InlineData("a b@example.com")]
    [InlineData("mona​@example.com")]
    public void InvalidEmail_IsRejected(string? email) =>
        AssertOnlyFieldFails(new CreateInstructorRequest("Mona Hassan", email), nameof(CreateInstructorRequest.Email));

    [Fact]
    public void Response_DoesNotPrintTheTemporaryPassword()
    {
        var response = new CreateInstructorResponse(Guid.NewGuid(), "Mona", "mona@university.edu", "Instructor", "Secret-Temp-42", DateTimeOffset.UtcNow);

        Assert.DoesNotContain("Secret-Temp-42", response.ToString());
    }

    private void AssertOnlyFieldFails(CreateInstructorRequest request, string field)
    {
        var result = _validator.Validate(request);

        Assert.False(result.IsValid);
        Assert.All(result.Errors, e => Assert.Equal(field, e.PropertyName));
    }
}
