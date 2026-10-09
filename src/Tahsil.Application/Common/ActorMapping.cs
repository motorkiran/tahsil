using Tahsil.Contracts;
using Tahsil.Domain.Common;

namespace Tahsil.Application.Common;

internal static class ActorMapping
{
    public static ActorResponse ToResponse(this Actor actor) => new(
        actor.Type switch
        {
            ActorType.ApiKey => ActorKind.ApiKey,
            ActorType.Admin => ActorKind.Admin,
            ActorType.Customer => ActorKind.Customer,
            _ => ActorKind.System,
        },
        actor.Id);
}
