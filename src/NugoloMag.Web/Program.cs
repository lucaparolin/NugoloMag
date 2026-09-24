using System.Globalization;
using NugoloMag.Analyst.Infrastructure.Store;
using NugoloMag.Web.Composition;

// Interfaccia e testi generati dall'agente in italiano (numeri e date), indipendentemente dal server.
CultureInfo.DefaultThreadCurrentCulture = CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.GetCultureInfo("it-IT");

var builder = WebApplication.CreateBuilder(args);
CompositionRoot.Register(builder.Services, builder.Configuration);

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

app.UseStaticFiles();
app.UseRouting();
app.MapControllerRoute("default", "{controller=Home}/{action=Index}/{id?}");

// Lo schema nugolo si crea (o si verifica) prima di accettare richieste e di avviare il monitoraggio.
await app.Services.GetRequiredService<StoreSchemaInstaller>().InstallAsync();

app.Run();
