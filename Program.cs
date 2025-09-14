using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Mvc; // ADD for ServiceFilterAttribute / filter helpers
using twoSaaSCore.Data;
using twoSaaSCore.Models;
using twoSaaSCore.Services;
using twoSaaSCore.Middleware;
using twoSaaSCore.Filters;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection") ?? throw new InvalidOperationException("Connection string 'DefaultConnection' not found.");
builder.Services.AddDbContext<ApplicationDbContext>(options =>
    options.UseSqlServer(connectionString));
builder.Services.AddDatabaseDeveloperPageExceptionFilter();

builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ITenantProvider, HttpContextTenantProvider>();

builder.Services.Configure<AzureBlobOptions>(builder.Configuration.GetSection("AzureBlobs"));
builder.Services.AddSingleton<IFileStorage, AzureBlobFileStorage>();

builder.Services.Configure<AzureBlobOptions>(builder.Configuration.GetSection("AzureBlob"));
builder.Services.AddSingleton<IRoomFileCatalog, BlobRoomFileCatalog>();
builder.Services.AddDefaultIdentity<ApplicationUser>(options =>
    {
        options.SignIn.RequireConfirmedAccount = true;
        options.Tokens.AuthenticatorTokenProvider = TokenOptions.DefaultAuthenticatorProvider;
        options.Lockout.MaxFailedAccessAttempts = 5;
    })
    .AddEntityFrameworkStores<ApplicationDbContext>()
    .AddDefaultTokenProviders(); // Adds Authenticator + Email + Phone token providers

// Register the MFA enforcement filter in DI
builder.Services.AddScoped<RequireAnyMfaFilter>();

builder.Services.AddRazorPages(options =>
{
//    // OPTION 1 (simplest): apply to every Razor Page
//    options.Filters.Add<RequireAnyMfaFilter>();

//OPTION 2(more selective): apply only to pages under root folder(comment OPTION 1 if using)
     options.Conventions.AddFolderApplicationModelConvention("/", model =>
     {
         // model.Filters is IList<IFilterMetadata>; AddService<T>() is NOT available here.
         // Use a ServiceFilterAttribute to get DI resolution.
         model.Filters.Add(new ServiceFilterAttribute(typeof(RequireAnyMfaFilter)));
     });

    // OPTION 3 (all pages via convention): uncomment if you prefer a single line
    // options.Conventions.ConfigureFilter(new ServiceFilterAttribute(typeof(RequireAnyMfaFilter)));
});

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseMigrationsEndPoint();
}
else
{
    app.UseExceptionHandler("/Error");
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseHttpsRedirection();

app.UseRouting();

app.UseAuthentication();
app.UseAuthorization();

app.UseMiddleware<SubdomainTenantMiddleware>();

app.MapStaticAssets();
app.MapRazorPages()
   .WithStaticAssets();

app.Run();
