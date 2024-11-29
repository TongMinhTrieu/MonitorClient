using ServerMonitor.Middlewares;
using ServerMonitor.Models;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;


public class SystemInfoService : BackgroundService
{
    private readonly string _webSocketUrl; // Địa chỉ server WebSocket
    private ClientWebSocket _webSocketClient;

    private readonly PerformanceCounter _cpuCounter;
    private readonly PerformanceCounter _ramCounter;
    private readonly int _reconnect; // Thời gian chờ giữa các lần thử kết nối lại
    private readonly int _period;   // Chu kỳ gửi

    public SystemInfoService(IConfiguration configuration)
    {
        _webSocketUrl = configuration["WebSocket:Url"];
        _period = int.Parse(configuration["Time:period"]);
        _reconnect = int.Parse(configuration["Time:reconnect"]);
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
                else
                {
                    // Gửi yêu cầu ping nếu WebSocket đang ở trạng thái Open
                    // Đảm bảo kết nối thực sự vẫn ổn
                    if (!await IsWebSocketStillConnected())
                    {
                        Console.WriteLine("WebSocket is not responding, reconnecting...");
                        await ConnectWebSocketAsync(stoppingToken);
                    }
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

            }
            catch (Exception ex)
            {
                Console.WriteLine("Error: " + ex.Message);
                // Nếu có lỗi, đợi một khoảng thời gian trước khi thử lại kết nối
                await Task.Delay(_reconnect, stoppingToken);
            }
            await Task.Delay(_period, stoppingToken); // Đảm bảo client tiếp tục kiểm tra mỗi giây
        }
    }
    private async Task<bool> IsWebSocketStillConnected()
    {
        try
        {
            if (_webSocketClient.State == WebSocketState.Open)
            {
                // Gửi một ping đơn giản đến server để xác nhận kết nối
                var buffer = new byte[1]; // Dữ liệu nhỏ để "ping"
                await _webSocketClient.SendAsync(new ArraySegment<byte>(buffer), WebSocketMessageType.Binary, true, CancellationToken.None);

                // Đợi phản hồi trong một khoảng thời gian hợp lý
                var receiveBuffer = new byte[1];
                var result = await _webSocketClient.ReceiveAsync(new ArraySegment<byte>(receiveBuffer), CancellationToken.None);

                // Nếu không có lỗi và có phản hồi, coi như WebSocket còn sống
                return result.MessageType != WebSocketMessageType.Close;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Ping error: {ex.Message}");
        }
        // Nếu có lỗi hoặc không nhận được phản hồi, coi như kết nối đã ngắt
        return false;
    }
    private async Task ConnectWebSocketAsync(CancellationToken stoppingToken)
    {
        // Kết nối lại WebSocket khi không có kết nối
        try
        {
            _webSocketClient = new ClientWebSocket();
            await _webSocketClient.ConnectAsync(new Uri(_webSocketUrl), stoppingToken);
            Console.WriteLine("WebSocket connected to " + _webSocketUrl);
        }
        catch (Exception ex)
        {
            Console.WriteLine("Error connecting to WebSocket: " + ex.Message);
            // Đợi một khoảng thời gian trước khi thử lại kết nối
            await Task.Delay(_reconnect, stoppingToken);
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
    public static string GetLocalIpAddress()
    {
        string localIp = string.Empty; // Địa chỉ mặc định nếu không tìm thấy IPv4
        foreach (var networkInterface in Dns.GetHostEntry(Dns.GetHostName()).AddressList)
        {
            // Kiểm tra địa chỉ IPv4 không phải loopback
            if (networkInterface.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(networkInterface))
            {
                localIp = networkInterface.ToString();
                break;
            }
        }
        return localIp;
    }
}
