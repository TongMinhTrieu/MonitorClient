using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Serilog;
using Serilog.Events;
using ServerMonitor.Middlewares;
using ServerMonitor.Services;
using System.ComponentModel;


var builder = WebApplication.CreateBuilder(args);
builder.Services.AddWindowsService(options =>
{
    options.ServiceName = "ServerMonitor";
});
// Add services to the container.
builder.Services.AddHostedService<SystemInfoService>();

builder.Host.UseSerilog((context, configuration) =>
{
    // Đường dẫn log động
    var logDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Logs");
    Directory.CreateDirectory(logDirectory); // Tạo thư mục nếu chưa tồn tại

    configuration
        .ReadFrom.Configuration(context.Configuration)
        .WriteTo.File(
            path: Path.Combine(logDirectory, "log-.txt"),
            rollingInterval: RollingInterval.Day,
            outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss} [{Level:u3}] {Message:lj}{NewLine}{Exception}");
});


builder.Services.AddControllers();

// Đăng ký PerformanceService và SqlRepository

builder.Services.AddEndpointsApiExplorer();

var app = builder.Build();

//app.UseMiddleware<ResponseTimeMiddleware>();
app.UseMiddleware<ErrorRateMiddleware>();
app.UseMiddleware<RequestCounterMiddleware>();


app.Run();

