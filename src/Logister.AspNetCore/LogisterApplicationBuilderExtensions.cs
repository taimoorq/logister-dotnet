using Microsoft.AspNetCore.Builder;

namespace Logister.AspNetCore;

public static class LogisterApplicationBuilderExtensions
{
    public static IApplicationBuilder UseLogisterExceptionReporting(this IApplicationBuilder app)
    {
        return app.UseMiddleware<LogisterExceptionMiddleware>();
    }

    public static IApplicationBuilder UseLogisterRequestTransactions(this IApplicationBuilder app)
    {
        return app.UseMiddleware<LogisterRequestTransactionMiddleware>();
    }
}
