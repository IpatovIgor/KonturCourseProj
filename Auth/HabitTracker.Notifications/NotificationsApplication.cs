using System.Text;
using HabitTracker.Messaging;
using HabitTracker.Notifications.Consumers;
using HabitTracker.Notifications.Data;
using HabitTracker.Notifications.Health;
using HabitTracker.Notifications.Hubs;
using HabitTracker.Notifications.Services;
using MassTransit;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Tokens;
using MongoDB.Driver;

namespace HabitTracker.Notifications;

public static class NotificationsApplication
{
    public static WebApplication Build(WebApplicationBuilder builder)
    {
        var config = builder.Configuration;
        var services = builder.Services;
        // Request-start logs run before middleware and can include the SignalR query token.
        builder.Logging.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Warning);
        builder.Logging.AddFilter("Microsoft.AspNetCore.Http.Connections", LogLevel.Warning);
        builder.Logging.AddFilter("Microsoft.AspNetCore.Diagnostics.ExceptionHandlerMiddleware", LogLevel.None);

        var key = Required(config, "JwtSettings:Secret");
        if (Encoding.UTF8.GetByteCount(key) < 32)
            throw new InvalidOperationException("JwtSettings__Secret must contain at least 32 bytes.");
        var issuer = Required(config, "JwtSettings:Issuer");
        var audience = Required(config, "JwtSettings:Audience");

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
        {
            options.MapInboundClaims = false;
            options.IncludeErrorDetails = false;
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
                ValidateIssuer = true,
                ValidIssuer = issuer,
                ValidateAudience = true,
                ValidAudience = audience,
                ValidateLifetime = true,
                ClockSkew = TimeSpan.Zero,
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key))
            };
            options.Events = new JwtBearerEvents
            {
                OnMessageReceived = context =>
                {
                    if (!context.Request.Headers.ContainsKey("Authorization") &&
                        context.Request.Path.StartsWithSegments(NotificationsHub.Path) &&
                        context.Request.Query["access_token"] is { Count: 1 } token)
                        context.Token = token.ToString();
                    return Task.CompletedTask;
                },
                OnTokenValidated = context =>
                {
                    if (SubjectUserIdProvider.GetUserId(context.Principal) is null)
                        context.Fail("Invalid JWT subject.");
                    return Task.CompletedTask;
                }
            };
        });
        services.AddAuthorization();
        services.AddSignalR();
        services.AddSingleton<IUserIdProvider, SubjectUserIdProvider>();
        services.TryAddSingleton<INotificationDelivery, NotificationDelivery>();
        services.TryAddSingleton(TimeProvider.System);

        var connectionString = Required(config, "MongoDbSettings:ConnectionString");
        var databaseName = Required(config, "MongoDbSettings:DatabaseName");
        services.AddSingleton<IMongoClient>(_ => new MongoClient(connectionString));
        services.AddSingleton(sp => sp.GetRequiredService<IMongoClient>().GetDatabase(databaseName));
        services.AddSingleton<NotificationRepository>();
        services.AddSingleton<MongoIndexInitializer>();
        services.AddHostedService(sp => sp.GetRequiredService<MongoIndexInitializer>());
        services.AddHealthChecks().AddCheck<NotificationsReadinessCheck>("notifications-storage",
            timeout: TimeSpan.FromSeconds(3));

        var rabbit = new RabbitMqSettings
        {
            Host = Required(config, "RabbitMq:Host"),
            VirtualHost = config["RabbitMq:VirtualHost"] ?? "habit-tracker",
            Username = Required(config, "RabbitMq:Username"),
            Password = Required(config, "RabbitMq:Password")
        };
        services.AddMassTransit(registration =>
        {
            registration.AddConsumer<NotificationReadyConsumer>();
            registration.UsingRabbitMq((context, bus) =>
            {
                rabbit.Configure(bus);
                bus.ReceiveEndpoint("notifications-ready-v1", endpoint =>
                {
                    endpoint.UseMessageRetry(retry =>
                    {
                        retry.Handle<MongoException>();
                        retry.Handle<TimeoutException>();
                        retry.Handle<NotificationDeliveryException>();
                        retry.Intervals(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15));
                    });
                    endpoint.ConfigureConsumer<NotificationReadyConsumer>(context);
                });
            });
        });

        services.AddProblemDetails();
        services.AddExceptionHandler<ApiExceptionHandler>();
        services.AddControllers().AddApplicationPart(typeof(NotificationsApplication).Assembly)
            .ConfigureApiBehaviorOptions(options =>
            {
                var invalid = options.InvalidModelStateResponseFactory;
                options.InvalidModelStateResponseFactory = context =>
                {
                    var response = invalid(context);
                    if (response is ObjectResult { Value: ValidationProblemDetails details })
                        details.Extensions["code"] = "validation_error";
                    return response;
                };
            });

        var app = builder.Build();
        app.UseExceptionHandler();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapControllers();
        app.MapHub<NotificationsHub>(NotificationsHub.Path, options => options.CloseOnAuthenticationExpiration = true);
        app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
        app.MapHealthChecks("/health/ready", new HealthCheckOptions
        {
            ResultStatusCodes =
            {
                [Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Degraded] = StatusCodes.Status503ServiceUnavailable
            }
        });
        return app;
    }

    private static string Required(IConfiguration config, string name) =>
        config[name] is { } value && !string.IsNullOrWhiteSpace(value)
            ? value : throw new InvalidOperationException($"Set {name.Replace(":", "__")}.");
}
