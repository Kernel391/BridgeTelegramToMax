using BridgeTelegramToMax.Services;

var builder = WebApplication.CreateBuilder(args);

// Telegram временно отключён: пока отлаживаем только приём сообщений из MAX.
// builder.Services.AddHostedService<BridgeTelegramToMaxService>();
builder.Services.AddSingleton<BridgeTelegramToMax.MaxConnector.MaxClient>();
builder.Services.AddHostedService<MaxListenerService>();
builder.Services.AddOptions<BridgeTelegramToMax.Configuration.MaxConfig>()
    .Bind(builder.Configuration.GetSection(BridgeTelegramToMax.Configuration.MaxConfig.SectionName))
    .ValidateDataAnnotations();

builder.Services.AddOptions<BridgeTelegramToMax.Configuration.TelegramConfig>()
    .Bind(builder.Configuration.GetSection(BridgeTelegramToMax.Configuration.TelegramConfig.SectionName))
    .ValidateDataAnnotations();

builder.Services.AddLogging(logging =>
{
    logging.ClearProviders();
    logging.AddConsole();
});

var app = builder.Build();
await app.RunAsync();
