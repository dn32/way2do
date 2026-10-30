namespace Way2Do.Api.Sync;

public static class SyncSetup
{
    /// <summary>Push e pull. O limite de corpo do push vale para toda a API (configurado no Kestrel, em Program).</summary>
    public static IServiceCollection AddSync(this IServiceCollection services) => services.AddScoped<SyncService>();
}
