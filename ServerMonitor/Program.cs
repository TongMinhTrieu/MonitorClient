using Microsoft.Extensions.DependencyInjection;
using ServerMonitor.Middlewares;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.

builder.Services.AddControllers();

// Đăng ký PerformanceService và SqlRepository

builder.Services.AddEndpointsApiExplorer();

builder.Services.AddHostedService<SystemInfoService>();

var app = builder.Build();
// Cấu hình WebSocket Middleware
app.UseWebSockets();
app.UseMiddleware<ApiMonitoringMiddleware>();
app.UseMiddleware<ResponseTimeMiddleware>();
app.UseMiddleware<ErrorRateMiddleware>();
app.UseMiddleware<RequestCounterMiddleware>();
app.UseMiddleware<WebSocketMiddleware>();

app.UseAuthorization();

app.MapControllers();
app.MapGet("/", () => "Hello World!");
app.Run();
