using FluentValidation;

namespace TransitTracker.Application.Queries.GetEta;

public sealed class GetEtaQueryValidator : AbstractValidator<GetEtaQuery>
{
    public GetEtaQueryValidator()
    {
        RuleFor(x => x.VehicleId).NotEmpty();
        RuleFor(x => x.StationId).NotEmpty();
    }
}
