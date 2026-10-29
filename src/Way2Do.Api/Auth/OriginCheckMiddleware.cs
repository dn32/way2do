namespace Way2Do.Api.Auth;

/// <summary>
/// Segunda barreira contra CSRF (a primeira é o cookie <c>SameSite=Strict</c>): requisições à API que alteram estado
/// e trazem um <c>Origin</c> fora da lista são recusadas. Sem <c>Origin</c>, a requisição não veio de uma página de
/// navegador em outro site (navegadores sempre o enviam em POST entre origens), então segue.
/// </summary>
public sealed class OriginCheckMiddleware(RequestDelegate next, AuthSettings settings)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (context.Request.Path.StartsWithSegments("/api") && ChangesState(context.Request.Method))
        {
            var origin = context.Request.Headers.Origin.ToString();
            if (origin.Length > 0 && !settings.AllowedOrigins.Contains(origin.TrimEnd('/')))
            {
                await Results.Problem(title: "Origem não permitida.", statusCode: StatusCodes.Status403Forbidden)
                    .ExecuteAsync(context);
                return;
            }
        }

        await next(context);
    }

    private static bool ChangesState(string method) =>
        !(HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method));
}
