using Microsoft.EntityFrameworkCore;
using CartSmart.API.Services;
using Microsoft.OpenApi.Models;
using DotNetEnv;
using Supabase;
using Microsoft.IdentityModel.Tokens;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using System.Threading.RateLimiting;

// Load environment variables
Env.Load();

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddMemoryCache();
builder.Services.AddDistributedMemoryCache();
builder.Services.AddHttpContextAccessor();

// ── Rate limiting ──
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    // Global per-IP policy: generous enough for normal browsing, blocks floods
    options.AddPolicy("per-ip", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 100,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0
            }));

    // Stricter policy for auth endpoints (login, register, password reset)
    options.AddPolicy("auth", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 10,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0
            }));
});

builder.Services.AddSession(options =>
{
    options.IdleTimeout = TimeSpan.FromMinutes(30);
    options.Cookie.HttpOnly = true;
    options.Cookie.IsEssential = true;
    options.Cookie.SameSite = SameSiteMode.None;
    options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
    options.Cookie.Name = "CartSmart.Session";
    options.Cookie.Path = "/";
});

builder.Services.AddControllers().AddNewtonsoftJson(options =>
{
    options.SerializerSettings.ReferenceLoopHandling = Newtonsoft.Json.ReferenceLoopHandling.Ignore;
});

// Add configuration sources
builder.Configuration
    .SetBasePath(Directory.GetCurrentDirectory())
    .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
    .AddJsonFile($"appsettings.{builder.Environment.EnvironmentName}.json", optional: true, reloadOnChange: true)
    .AddEnvironmentVariables();

// Add services to the container.
builder.Services.AddControllers();
builder.Services.AddResponseCaching();
builder.Services.AddEndpointsApiExplorer();

// Configure Swagger (dev only)
if (builder.Environment.IsDevelopment())
{
    builder.Services.AddSwaggerGen(c =>
    {
        c.SwaggerDoc("v1", new OpenApiInfo { Title = "CartSmart API", Version = "v1" });
        
        // Add JWT Authentication
        c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
        {
            Description = "JWT Authorization header using the Bearer scheme",
            Name = "Authorization",
            In = ParameterLocation.Header,
            Type = SecuritySchemeType.ApiKey,
            Scheme = "Bearer"
        });

        c.AddSecurityRequirement(new OpenApiSecurityRequirement
        {
            {
                new OpenApiSecurityScheme
                {
                    Reference = new OpenApiReference
                    {
                        Type = ReferenceType.SecurityScheme,
                        Id = "Bearer"
                    }
                },
                Array.Empty<string>()
            }
        });
    });
}

// Add Authentication
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        var jwtSecret = builder.Configuration["Jwt:Secret"]
                        ?? builder.Configuration["Authentication:Jwt:Secret"];
        if (string.IsNullOrEmpty(jwtSecret))
            throw new InvalidOperationException("JWT Secret not configured");

        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.ASCII.GetBytes(jwtSecret)),
            ValidateIssuer = false,
            ValidateAudience = false,
            ClockSkew = TimeSpan.Zero
        };

        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                if (string.IsNullOrEmpty(context.Token) &&
                    context.Request.Cookies.TryGetValue("access_token", out var token))
                {
                    context.Token = token;
                }
                return Task.CompletedTask;
            }
        };
    });

// Configure port - only use HTTPS in development
if (builder.Environment.IsDevelopment())
{
    builder.WebHost.UseUrls("http://localhost:5000", "https://localhost:5001");
}
else
{
    // Azure App Service provides the PORT environment variable
    var port = Environment.GetEnvironmentVariable("PORT") ?? "8080";
    builder.WebHost.UseUrls($"http://+:{port}");
}

// Add Supabase Service
builder.Services.AddScoped<ISupabaseService, SupabaseService>();

// Register services in correct order
builder.Services.AddScoped<IAuthService, AuthService>();
builder.Services.AddScoped<IUserService, UserService>();
builder.Services.AddScoped<IDealService, DealService>();
builder.Services.AddScoped<IProductService, ProductService>();
builder.Services.AddScoped<IStoreDealsService, StoreDealsService>();
builder.Services.AddScoped<IUserReputationService, UserReputationService>();
// Unified email service: choose SendGrid if configured else fallback to SMTP
builder.Services.AddScoped<IEmailService, SendGridEmailService>();
builder.Services.AddScoped<IUserTokenService, UserTokenService>();
builder.Services.AddScoped<INotificationService, NotificationService>();

// Add Social Login Services
if (!string.IsNullOrEmpty(builder.Configuration["Authentication:Google:ClientId"]))
{
    builder.Services.AddScoped<IGoogleAuthService, GoogleAuthService>();
}
else
{
    builder.Services.AddScoped<IGoogleAuthService, NullGoogleAuthService>();
}

if (!string.IsNullOrEmpty(builder.Configuration["Authentication:Apple:ClientId"]))
{
    builder.Services.AddScoped<IAppleAuthService, AppleAuthService>();
}
else
{
    builder.Services.AddScoped<IAppleAuthService, NullAppleAuthService>();
}

// Add CORS
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowReactApp",
        builder => builder
            .WithOrigins(
                "https://cartsmart.com",
                "https://www.cartsmart.com",
                "http://localhost:3000",
                "http://localhost:5000",
                "https://localhost:5001"
            )
            .AllowAnyMethod()
            .AllowAnyHeader()
            .AllowCredentials()
            .WithExposedHeaders("Set-Cookie"));
});

builder.Services.AddSingleton<IUrlSanitizer, UrlSanitizer>();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(c =>
    {
        c.SwaggerEndpoint("/swagger/v1/swagger.json", "CartSmart API V1");
        c.RoutePrefix = "swagger";
    });
}

// Use routing before CORS/endpoints
app.UseRouting();

// Rate limiting (before CORS/auth so floods are rejected early)
app.UseRateLimiter();

// CORS must run between routing and endpoints to apply to controllers
app.UseCors("AllowReactApp");
app.UseMiddleware<ActiveUserMiddleware>();
app.UseAuthentication();
app.UseAuthorization();
app.UseResponseCaching();

app.Use(async (context, next) =>
{
    // Allow Google OAuth popup messaging
    context.Response.Headers["Cross-Origin-Opener-Policy"] = "same-origin-allow-popups";
    // Security headers
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["X-Frame-Options"] = "DENY";
    context.Response.Headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
    context.Response.Headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
    await next();
});



// Use endpoints after routing and auth
app.UseEndpoints(endpoints =>
{
    endpoints.MapControllers().RequireRateLimiting("per-ip");
});

app.UseDefaultFiles();   // lets index.html be served automatically
app.UseStaticFiles();    // serve files in wwwroot (your CRA build goes here)

app.MapControllers();    // maps your API controllers

// fallback: if no controller/static file matched, return index.html
app.MapFallbackToFile("index.html");

app.Run();