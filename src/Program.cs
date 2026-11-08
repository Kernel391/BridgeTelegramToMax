using BridgeTelegramToMax.Bridge;
using BridgeTelegramToMax.Configuration;
using BridgeTelegramToMax.MaxConnector;
using Microsoft.Extensions.Options;
using Telegram.Bot;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOptions<MaxConfig>()
    .Bind(builder.Configuration.GetSection(MaxConfig.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddOptions<TelegramConfig>()
    .Bind(builder.Configuration.GetSection(TelegramConfig.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddSingleton<ITelegramBotClient>(services =>
{
    var config = services.GetRequiredService<IOptions<TelegramConfig>>().Value;
    var botOptions = new TelegramBotClientOptions(config.ApiToken);
    return new TelegramBotClient(botOptions, new HttpClient { Timeout = TimeSpan.FromMinutes(30) });
});

builder.Services.AddSingleton<MaxClient>();
builder.Services.AddSingleton<TopicStore>();
builder.Services.AddSingleton<GeneralNotifier>();
builder.Services.AddSingleton<TelegramMediaSender>();
builder.Services.AddSingleton<MaxToTelegramForwarder>();
builder.Services.AddSingleton<TelegramToMaxHandler>();
builder.Services.AddHostedService<BridgeService>();

builder.Services.AddLogging(logging =>
{
    logging.ClearProviders();
    logging.AddConsole();
});

var app = builder.Build();
await app.RunAsync();
