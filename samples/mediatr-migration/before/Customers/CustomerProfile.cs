using MediatR;

namespace Customers;

public sealed record CustomerProfile(string Id, string Email);

public sealed record GetCustomerProfileQuery(string Id) : IRequest<CustomerProfile>;

// Partial handler split across CustomerProfile.cs + CustomerProfilePart2.cs. Both files carry the
// MediatR using directive, so ROGM001 fires twice (overlapping edits across documents); the Handle
// method (in part 2) triggers ROGM002 once. The fixed-point "Fix All in Project" loop must converge
// across both documents.
public sealed partial class CustomerProfileQueryHandler
    : IRequestHandler<GetCustomerProfileQuery, CustomerProfile>
{
}
