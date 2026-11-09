using BackEnd.Controllers;
using BackEnd.Data;
using BackEnd.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using MongoDB.Bson;
using MongoDB.Driver;
using Swashbuckle.AspNetCore.SwaggerGen;
using System.IdentityModel.Tokens.Jwt;
using System.Text;

var builder = WebApplication.CreateBuilder(args);

// 1. Подключаем настройки из appsettings.json
builder.Services.Configure<MongoDbSettings>(
    builder.Configuration.GetSection("MongoDbSettings"));

builder.Services.AddOptions<JwtSettings>()
    .Bind(builder.Configuration.GetSection("JwtSettings"))
    .Validate(settings => !string.IsNullOrWhiteSpace(settings.Secret) && Encoding.UTF8.GetByteCount(settings.Secret) >= 32,
        "Set JwtSettings__Secret to a private signing key of at least 32 bytes.")
    .ValidateOnStart();

// 2. Регистрируем наши сервисы и репозиторий
// ВАЖНО: Регистрируем интерфейс IUserRepository, а реализацией назначаем UserRepository
builder.Services.AddSingleton<IMongoClient>(services =>
    new MongoClient(services.GetRequiredService<IOptions<MongoDbSettings>>().Value.ConnectionString));
builder.Services.AddSingleton(services =>
    services.GetRequiredService<IMongoClient>().GetDatabase(
        services.GetRequiredService<IOptions<MongoDbSettings>>().Value.DatabaseName));
builder.Services.AddSingleton<UserRepository>();
builder.Services.AddSingleton<IUserRepository>(services => services.GetRequiredService<UserRepository>());
builder.Services.AddSingleton<HabitRepository>();
builder.Services.AddHostedService<MongoIndexInitializer>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<HabitService>();
builder.Services.AddScoped<AuthService>();

// 3. Добавляем поддержку контроллеров
builder.Services.AddControllers().ConfigureApiBehaviorOptions(options =>
{
    var original = options.InvalidModelStateResponseFactory;
    options.InvalidModelStateResponseFactory = context =>
    {
        var response = original(context);
        if (IsHabitsAction(context.HttpContext) &&
            response is ObjectResult { Value: ValidationProblemDetails problem })
        {
            problem.Extensions["code"] = "validation_error";
        }
        return response;
    };
});
builder.Services.AddAuthorization();

// 4. Настройка JWT Аутентификации
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.MapInboundClaims = false;
        options.Events = new JwtBearerEvents
        {
            OnTokenValidated = context =>
            {
                if (IsHabitsAction(context.HttpContext) &&
                    !ObjectId.TryParse(context.Principal?.FindFirst(JwtRegisteredClaimNames.Sub)?.Value, out _))
                    context.Fail("Invalid JWT subject.");
                return Task.CompletedTask;
            },
            OnChallenge = async context =>
            {
                if (!IsHabitsAction(context.HttpContext))
                    return;

                context.HandleResponse();
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                context.Response.Headers.WWWAuthenticate = "Bearer";
                await context.Response.WriteAsJsonAsync(HabitsController.ToProblem(HabitApiException.Unauthenticated()),
                    options: null, contentType: "application/problem+json",
                    cancellationToken: context.HttpContext.RequestAborted);
            }
        };
    });
builder.Services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
    .Configure<IOptions<JwtSettings>>((options, settings) =>
    {
        var jwtSettings = settings.Value;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = jwtSettings.Issuer,
            ValidAudience = jwtSettings.Audience,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSettings.Secret))
        };
    });

// 5. Добавляем Swagger
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo 
    { 
        Title = "Habit Tracker API",
        Version = "v1" 
    });
    c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Type = SecuritySchemeType.ApiKey,
        Name = "Authorization",
        In = ParameterLocation.Header,
        Description = "Введите Bearer <JWT>."
    });
    c.OperationFilter<BearerOperationFilter>();
});

var app = builder.Build();

// 6. Включаем Swagger с принудительным OpenAPI 2.0 (чтобы UI не выдавал ошибку)
if (app.Environment.IsDevelopment())
{
    app.UseSwagger(c =>
    {
        c.SerializeAsV2 = true;
    });
    
    app.UseSwaggerUI(c =>
    {
        c.SwaggerEndpoint("/swagger/v1/swagger.json", "Habit Tracker API v1");
    });
}

app.UseHttpsRedirection();

// 7. Подключаем аутентификацию и авторизацию
app.UseAuthentication();
app.UseAuthorization();

// 8. Включаем маршрутизацию запросов к контроллерам
app.MapControllers();

// 9. Запускаем приложение
app.Run();

static bool IsHabitsAction(HttpContext context) =>
    context.GetEndpoint()?.Metadata.GetMetadata<ControllerActionDescriptor>()?.ControllerTypeInfo.AsType()
    == typeof(HabitsController);

internal sealed class BearerOperationFilter : IOperationFilter
{
    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        if (context.MethodInfo.DeclaringType != typeof(HabitsController))
            return;

        operation.Security.Add(new OpenApiSecurityRequirement
        {
            [new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" }
            }] = Array.Empty<string>()
        });
    }
}
