using ModernizationEngineUI.Components;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// Singleton: the Home <-> History navigation links use data-enhance-nav="false" (required to
// avoid Blazor enhanced-nav corrupting the Monaco editor's DOM), which forces a full page reload
// and therefore a brand-new Blazor Server circuit on every navigation. A Scoped (per-circuit)
// service would lose its history on every hop between pages, so this needs to be Singleton
// instead. Trade-off: history is now shared app-wide rather than isolated per browser session -
// acceptable for this single-user demo tool, but would need revisiting for multi-user hosting.
builder.Services.AddSingleton<ModernizationHistoryService>();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
