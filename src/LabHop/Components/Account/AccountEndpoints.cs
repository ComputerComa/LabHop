using LabHop.Data;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Identity;

namespace LabHop.Components.Account;

internal static class AccountEndpoints
{
    public static void MapAccountEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/Account/Logout", async (HttpContext context, IAntiforgery antiforgery, SignInManager<ApplicationUser> signInManager) =>
        {
            try
            {
                await antiforgery.ValidateRequestAsync(context);
            }
            catch (AntiforgeryValidationException)
            {
                return Results.BadRequest();
            }

            await signInManager.SignOutAsync();
            return Results.LocalRedirect("~/Account/Login");
        });
    }
}
