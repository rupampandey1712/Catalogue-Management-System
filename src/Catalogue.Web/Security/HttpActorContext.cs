using System.Security.Claims;
using Catalogue.Application;

namespace Catalogue.Web.Security;

public sealed class HttpActorContext(IHttpContextAccessor http) : IActorContext
{
    public string ActorId =>
        http.HttpContext?.User.FindFirst(ClaimTypes.NameIdentifier)?.Value
        ?? http.HttpContext?.User.Identity?.Name
        ?? "anonymous";

    public string CorrelationId =>
        CorrelationContext.Current
        ?? http.HttpContext?.Items["CorrelationId"] as string
        ?? CorrelationContext.NewId();
}
