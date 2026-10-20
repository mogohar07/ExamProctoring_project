using ExamProctoring.Api.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ExamProctoring.IntegrationTests.TestSupport;

// Test-only endpoints, added to the test host by ApiFactory, so the policies can be checked before any real
// endpoint uses them. Each returns 200 when authorization lets the request through.
[ApiController]
[Route("test-only/authorization")]
public sealed class AuthorizationProbeController : ControllerBase
{
    // No authorization attribute: the fallback policy applies.
    [HttpGet("fallback")]
    public IActionResult Fallback() => Ok();

    [HttpGet("admin")]
    [Authorize(Policy = AuthorizationPolicies.AdminOnly)]
    public IActionResult Admin() => Ok();

    [HttpGet("instructor")]
    [Authorize(Policy = AuthorizationPolicies.InstructorOnly)]
    public IActionResult Instructor() => Ok();

    [HttpGet("student")]
    [Authorize(Policy = AuthorizationPolicies.StudentOnly)]
    public IActionResult Student() => Ok();
}
