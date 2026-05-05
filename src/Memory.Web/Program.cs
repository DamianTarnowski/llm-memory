using Memory.Web;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

var tenantSettings = new TenantSettings();
builder.Configuration.GetSection("Tenant").Bind(tenantSettings);
builder.Services.AddSingleton(tenantSettings);

builder.Services.AddSingleton<AuthService>();
builder.Services.AddTransient<BearerTokenHandler>();

builder.Services
    .AddHttpClient<ApiClient>(client => client.BaseAddress = new Uri(tenantSettings.ApiBaseUrl))
    .AddHttpMessageHandler<BearerTokenHandler>();

var host = builder.Build();
// Hydrate the auth token from localStorage before the first render so refreshes
// don't briefly run as the unauthenticated dev fallback.
var auth = host.Services.GetRequiredService<AuthService>();
await auth.LoadAsync();
await host.RunAsync();
