using Memory.Web;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

var tenantSettings = new TenantSettings();
builder.Configuration.GetSection("Tenant").Bind(tenantSettings);
builder.Services.AddSingleton(tenantSettings);

builder.Services.AddTransient<TenantHeaderHandler>();

builder.Services
    .AddHttpClient<ApiClient>(client => client.BaseAddress = new Uri(tenantSettings.ApiBaseUrl))
    .AddHttpMessageHandler<TenantHeaderHandler>();

await builder.Build().RunAsync();
