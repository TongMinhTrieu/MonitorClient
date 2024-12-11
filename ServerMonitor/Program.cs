using Microsoft.Extensions.DependencyInjection;
using ServerMonitor.Middlewares;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddWindowsService(options =>
{
    options.ServiceName = "ServerMonitor";
});
// Add services to the container.
builder.Services.AddHostedService<SystemInfoService>();
builder.Services.AddControllers();

// Đăng ký PerformanceService và SqlRepository

builder.Services.AddEndpointsApiExplorer();

IHost app = builder.Build();

app.Run();
