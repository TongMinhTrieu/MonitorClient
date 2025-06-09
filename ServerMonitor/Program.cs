using Microsoft.Extensions.Caching.Memory;
using Serilog;
using Serilog.Core;
using ServerMonitor.Datas;
using ServerMonitor.Middlewares;
using ServerMonitor.Services;

Directory.SetCurrentDirectory(AppDomain.CurrentDomain.BaseDirectory);
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddWindowsService(options =>
{
    options.ServiceName = "ServerMonitor";
});
builder.Services.AddHttpClient();
builder.Services.AddMemoryCache();

// Add services to the container.
builder.Services.AddSingleton<ILogService, LogService>();

builder.Services.AddHostedService<SystemInfoService>();
builder.Services.AddHostedService<TcpCheckService>();

GlobalData.TryGetLogPath(builder.Configuration);
Logger logger = new LoggerConfiguration().ReadFrom.Configuration(builder.Configuration).Enrich.FromLogContext().CreateLogger();
builder.Logging.ClearProviders();
builder.Logging.AddSerilog(logger);


builder.Services.AddControllers();

// Đăng ký PerformanceService và SqlRepository

builder.Services.AddEndpointsApiExplorer();

var app = builder.Build();
GlobalData.Cache = app.Services.GetRequiredService<IMemoryCache>();
//app.UseMiddleware<ResponseTimeMiddleware>();
app.UseMiddleware<ErrorRateMiddleware>();
app.UseMiddleware<RequestCounterMiddleware>();


app.Run();

