using Microsoft.Extensions.Caching.Memory;
using System.Diagnostics;

namespace ServerMonitor.Datas;

public static class GlobalData
{
    public static IMemoryCache Cache { get; set; } = null!;
    public static List<string> LogDirectories { get; set; } = [];

    public static void TryGetLogPath(ConfigurationManager config)
    {
        try
        {
            var writeTo = config.GetSection("Serilog:WriteTo").GetChildren();
            foreach (var logger in writeTo)
            {
                string? path = logger.GetSection("Args:configureLogger:WriteTo:0:Args:path").Value;
                if (!string.IsNullOrEmpty(path))
                {
                    // Chuyển đường dẫn tương đối thành tuyệt đối DỰA TRÊN THƯ MỤC ỨNG DỤNG
                    string absolutePath = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, path));
                    string? directory = Path.GetDirectoryName(absolutePath);
                    if (!string.IsNullOrEmpty(directory))
                    {
                        LogDirectories.Add(directory);
                        if (!Directory.Exists(directory))
                        {
                            Directory.CreateDirectory(directory);
                        }
                        // CẬP NHẬT LẠI GIÁ TRỊ TRONG CONFIG ĐỂ SERILOG SỬ DỤNG
                        logger.GetSection("Args:configureLogger:WriteTo:0:Args:path").Value = absolutePath;
                    }
                }
            }
        }
        catch
        {
            LogDirectories.Clear();
        }
    }
}
