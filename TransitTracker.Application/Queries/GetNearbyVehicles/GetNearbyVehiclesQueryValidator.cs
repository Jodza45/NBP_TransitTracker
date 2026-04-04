using FluentValidation;

namespace TransitTracker.Application.Queries.GetNearbyVehicles;

public sealed class GetNearbyVehiclesQueryValidator : AbstractValidator<GetNearbyVehiclesQuery>
{
    public GetNearbyVehiclesQueryValidator()
    {
        RuleFor(x => x.Latitude).InclusiveBetween(-90, 90);
        RuleFor(x => x.Longitude).InclusiveBetween(-180, 180);
        RuleFor(x => x.RadiusMeters).GreaterThan(0);
    }
}
