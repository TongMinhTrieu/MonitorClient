using Microsoft.Extensions.DependencyInjection;
using ServerMonitor.Middlewares;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddWindowsService(options =>
{
    options.ServiceName = "ServerMonitor";
});
// Add services to the container.
builder.Services.AddHostedService<SystemInfoService>();
builder.Services.AddControllers();

// Đăng ký PerformanceService và SqlRepository

builder.Services.AddEndpointsApiExplorer();

var app = builder.Build();
app.UseMiddleware<ApiMonitoringMiddleware>();
app.UseMiddleware<ErrorRateMiddleware>();
app.UseMiddleware<RequestCounterMiddleware>();
app.UseMiddleware<ResponseTimeMiddleware>();
app.UseMiddleware<WebSocketMiddleware>();

app.Run();
