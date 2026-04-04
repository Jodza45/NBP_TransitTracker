using FluentValidation;

namespace TransitTracker.Application.Commands.UpdateVehicleLocation;

public sealed class UpdateVehicleLocationCommandValidator : AbstractValidator<UpdateVehicleLocationCommand>
{
    public UpdateVehicleLocationCommandValidator()
    {
        RuleFor(x => x.VehicleId).NotEmpty();
        RuleFor(x => x.Latitude).InclusiveBetween(-90, 90);
        RuleFor(x => x.Longitude).InclusiveBetween(-180, 180);
        RuleFor(x => x.Timestamp).NotEmpty();
    }
}
