using FluentValidation;

namespace TransitTracker.Application.Queries.GetVehicleLocation;

public sealed class GetVehicleLocationQueryValidator : AbstractValidator<GetVehicleLocationQuery>
{
    public GetVehicleLocationQueryValidator()
    {
        RuleFor(x => x.VehicleId).NotEmpty();
    }
}
