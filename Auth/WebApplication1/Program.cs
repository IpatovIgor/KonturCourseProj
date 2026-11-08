using BackEnd.Data;
using BackEnd.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using System.Text;

var builder = WebApplication.CreateBuilder(args);

// 1. Подключаем настройки из appsettings.json
builder.Services.Configure<MongoDbSettings>(
    builder.Configuration.GetSection("MongoDbSettings"));

builder.Services.Configure<JwtSettings>(
    builder.Configuration.GetSection("JwtSettings"));

// 2. Регистрируем наши сервисы и репозиторий
// ВАЖНО: Регистрируем интерфейс IUserRepository, а реализацией назначаем UserRepository
builder.Services.AddSingleton<IUserRepository, UserRepository>();
builder.Services.AddScoped<AuthService>();

// 3. Добавляем поддержку контроллеров
builder.Services.AddControllers();

// 4. Настройка JWT Аутентификации
var jwtSettings = builder.Configuration.GetSection("JwtSettings").Get<JwtSettings>();
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = jwtSettings!.Issuer,
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
        Title = "Auth Service API", 
        Version = "v1" 
    });
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
        c.SwaggerEndpoint("/swagger/v1/swagger.json", "Auth Service API v1");
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