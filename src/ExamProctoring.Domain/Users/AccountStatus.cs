namespace ExamProctoring.Domain.Users;

// Stored as a string (§5.1). No Pending/Rejected states: accounts are not approved (§2.2).
public enum AccountStatus
{
    Active,
    Deactivated
}
