using ServerMonitor.Middlewares;
using ServerMonitor.Models;
using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Net.WebSockets;
using System.Text;


public class SystemInfoService : BackgroundService
{
    private readonly string _webSocketUrl; // Địa chỉ server WebSocket
    private ClientWebSocket _webSocketClient;

    private readonly PerformanceCounter _cpuCounter;
    private readonly PerformanceCounter _ramCounter;
    private readonly TimeSpan _reconnectInterval = TimeSpan.FromSeconds(5); // Thời gian chờ giữa các lần thử kết nối lại
    public SystemInfoService(IConfiguration configuration)
    {
        _webSocketUrl = configuration["WebSocket:Url"];
        _cpuCounter = new PerformanceCounter("Processor", "% Processor Time", "_Total");
        _ramCounter = new PerformanceCounter("Memory", "Available MBytes");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Kiểm tra trạng thái kết nối và kết nối lại nếu cần
                if (_webSocketClient == null || _webSocketClient.State != WebSocketState.Open)
                {
                    Console.WriteLine("Attempting to connect to WebSocket server...");
                    await ConnectWebSocketAsync(stoppingToken);
                }
                // Lấy địa chỉ IP của client
                string clientIp = GetLocalIpAddress();

                // Tạo dữ liệu giám sát
                var cpuUsage = _cpuCounter.NextValue();
                var memoryUsage = _ramCounter.NextValue();

                var driveInfo = new DriveInfo("C");
                var freeSpace = driveInfo.AvailableFreeSpace / (1024 * 1024 * 1024);
                var totalSpace = driveInfo.TotalSize / (1024 * 1024 * 1024);

                var networkStats = GetNetworkStats();
                var apiStats = ApiMonitoringMiddleware.GetApiStatistics();
                var formattedStats = apiStats.Select(kvp => new { Api = kvp.Key, Calls = kvp.Value }).ToList();

                // Chuẩn bị dữ liệu JSON để gửi qua WebSocket
                var systemInfo = new
                {
                    ClientIp = clientIp,
                    CpuUsage = $"{cpuUsage:F2}",
                    MemoryAvailable = $"{memoryUsage:F2}",
                    DiskFreeSpace = $"{freeSpace:F2}",
                    DiskTotalSpace = $"{totalSpace:F2}",
                    NetworkSpeed = $"{networkStats:F2}",
                    ApiStatistics = formattedStats,
                    DateStamp = DateTime.Now
                };

                var jsonMessage = System.Text.Json.JsonSerializer.Serialize(systemInfo);

                // Gửi dữ liệu qua WebSocket nếu kết nối mở
                if (_webSocketClient.State == WebSocketState.Open)
                {
                    var messageBytes = Encoding.UTF8.GetBytes(jsonMessage);
                    await _webSocketClient.SendAsync(
                        new ArraySegment<byte>(messageBytes),
                        WebSocketMessageType.Text,
                        true,
                        stoppingToken
                    );
                    Console.WriteLine("Sent: " + jsonMessage);
                }
                else
                {
                    Console.WriteLine("WebSocket is not connected.");
                }

                // Reset bộ đếm lần gọi API mỗi phút
                RequestCounterMiddleware.ResetCounter();
                await Task.Delay(2000, stoppingToken); // Cập nhật thời gian gửi theo chu kỳ (mỗi 2 giây)
            }
            catch (Exception ex)
            {
                Console.WriteLine("Error: " + ex.Message);
                // Nếu có lỗi, đợi một khoảng thời gian trước khi thử lại kết nối
                await Task.Delay(_reconnectInterval, stoppingToken);
            }
        }
    }
    private async Task ConnectWebSocketAsync(CancellationToken stoppingToken)
    {
        _webSocketClient = new ClientWebSocket();

        try
        {
            // Cố gắng kết nối tới WebSocket server
            await _webSocketClient.ConnectAsync(new Uri(_webSocketUrl), stoppingToken);
            Console.WriteLine("WebSocket connected to " + _webSocketUrl);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error connecting to WebSocket: {ex.Message}");
        }
    }
    private string GetNetworkStats()
    {
        try
        {
            var networkInterfaces = NetworkInterface.GetAllNetworkInterfaces();
            foreach (var ni in networkInterfaces)
            {
                if (ni.OperationalStatus == OperationalStatus.Up)
                {
                    var statistics = ni.GetIPv4Statistics();
                    var speedMbps = ni.Speed / (1024.0 * 1024.0); // Mbps
                    return $"{speedMbps:F2}";
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"{ex.Message}");
        }

        return "N/A";
    }
    public string GetLocalIpAddress()
    {
        string localIp = string.Empty;
        foreach (var networkInterface in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
        {
            var ipProperties = networkInterface.GetIPProperties();
            foreach (var ipAddress in ipProperties.UnicastAddresses)
            {
                if (ipAddress.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                {
                    localIp = ipAddress.Address.ToString();
                    break;
                }
            }
        }
        return localIp;
    }
}