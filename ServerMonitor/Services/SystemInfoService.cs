using ServerMonitor.Middlewares;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System;
using System.Collections.Generic;
using Microsoft.Data.SqlClient;
using System.Management;
using PacketDotNet;
using SharpPcap;
using PacketDotNet.Ieee80211;
using System.Text.RegularExpressions;
using static System.Net.Mime.MediaTypeNames;
using static System.Net.WebRequestMethods;
using System.Globalization;


public class SystemInfoService : BackgroundService
{
    private readonly string _webSocketUrl; // Địa chỉ server WebSocket
    private ClientWebSocket _webSocketClient;

    private readonly PerformanceCounter _cpuCounter;
    private readonly PerformanceCounter _ramCounter;
    private readonly int _reconnect; // Thời gian chờ giữa các lần thử kết nối lại
    private readonly int _period;   // Chu kỳ gửi
    private readonly string _adapter;
    private readonly ILogger<SystemInfoService> _logger;

    private readonly string _connectionstring;
   
    private Dictionary<string, int> _requestCounts = new Dictionary<string, int>();

    public SystemInfoService(IConfiguration configuration, ILogger<SystemInfoService> logger)
    {
        _webSocketUrl = configuration["WebSocket:Url"];
        _period = int.Parse(configuration["Time:period"]);
        _reconnect = int.Parse(configuration["Time:reconnect"]);
        _cpuCounter = new PerformanceCounter("Processor", "% Processor Time", "_Total");
        _ramCounter = new PerformanceCounter("Memory", "Available MBytes");
        _connectionstring = configuration["ConnectionStrings:Default"];
        _adapter = configuration["Adapter:Name"];
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        int count = 0;
        // Chọn adapter đầu tiên (có thể thay đổi logic tùy nhu cầu)
        ICaptureDevice selectedAdapter = GetAdapterWithKeyword();
        var totalMemory = GetTotalRAM();
        var listDatabases = GetDatabases(_connectionstring);

        while (!stoppingToken.IsCancellationRequested)
        {
            if (count == 300) //10 phút lấy thông số database 1 lần
            {
                listDatabases = GetDatabases(_connectionstring);
                count = 0;
            }
            count++;
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

                // Tạo một luồng riêng để bắt gói tin
                if (selectedAdapter != null)
                {
                    selectedAdapter.StopCapture();
                    selectedAdapter.Close();
                    // Sau đó có thể mở và sử dụng adapter này
                    selectedAdapter.Open();

                    // Nếu cần bắt gói tin
                    selectedAdapter.OnPacketArrival += new PacketArrivalEventHandler(Device_OnPacketArrival);
                    selectedAdapter.StartCapture();                   
                }
                else
                {
                    Console.WriteLine("No suitable adapter found.");
                    _logger.LogWarning("Không tìm thấy Adapter phù hợp");
                }

                // Lấy địa chỉ IP của client
                string clientIp = GetLocalIpAddress();

                // Tạo dữ liệu giám sát
                var cpuUsage = _cpuCounter.NextValue();

                var memoryUsage = _ramCounter.NextValue();

                // Lấy danh sách tất cả các ổ đĩa
                var drives = DriveInfo.GetDrives();

                List<(string, long, long)> driverInfo = new List<(string, long, long)>();

                foreach (var drive in drives)
                {
                    // Kiểm tra xem ổ đĩa có sẵn (IsReady) hay không
                    if (drive.IsReady)
                    {
                        var info = (drive.Name, drive.AvailableFreeSpace / (1024 * 1024 * 1024), drive.TotalSize / (1024 * 1024 * 1024));
                        driverInfo.Add(info);
                    }
                }

                var networkStats = GetNetworkSpeed();
                // Chuyển đổi dictionary thành danh sách (hoặc một chuỗi, nếu cần)
                var networkSpeedFormatted = networkStats.Select(ni => new
                {
                    NetworkInterface = ni.Key,
                    ReceiveSpeed = $"{ni.Value.receiveKbps:F2} Kbps",
                    SendSpeed = $"{ni.Value.sendKbps:F2} Kbps"
                }).ToList();

                // Chuyển đổi tuple thành danh sách đối tượng dynamic
                var dynamicDatabases = listDatabases.Select(db => new { DatabaseName = db.Item1, TotalCurrentSizeMB = db.Item2, FreeSpaceMB = db.Item3 }).ToList();

                var dynamicDriverInfo = driverInfo.Select(info => new { DriverName = info.Item1, DiskFreeSpace = info.Item2, DiskTotalSpace = info.Item3 }).ToList();

                // Chuẩn bị dữ liệu JSON để gửi qua WebSocket
                var systemInfo = new
                {
                    ClientIp = clientIp,
                    DateStamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                    CpuUsage = $"{cpuUsage:F2}",                    
                    MemoryAvailable = $"{memoryUsage:F2}",
                    MemoryTotal = $"{totalMemory:F2}",
                    DiskInfo = dynamicDriverInfo,
                    NetworkSpeed = networkSpeedFormatted,
                    ApiStatistics = _requestCounts,
                    ListDatabases = dynamicDatabases
                };

                var jsonMessage = System.Text.Json.JsonSerializer.Serialize(systemInfo);

                // Gửi dữ liệu qua WebSocket nếu kết nối mở
                if (_webSocketClient != null )
                {
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

                        // Clear the byte array to help the garbage collector reclaim memory more quickly
                        Array.Clear(messageBytes, 0, messageBytes.Length);
                        _requestCounts.Clear();
                    }

                    else
                    {
                        Console.WriteLine("WebSocket is not connected.");
                        _logger.LogWarning("WebSocket mất kết nối");
                    }
                }               
            }
            catch (Exception ex)
            {
                Console.WriteLine("Error: " + ex.Message);
                _logger.LogError($"Lỗi: {ex.Message}");
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
            _logger.LogError($"Ping lỗi: {ex.Message}");
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
            _logger.LogInformation($"Kết nối đến Socket: {_webSocketUrl}");
        }
        catch (Exception ex)
        {
            Console.WriteLine("Error connecting to WebSocket: " + ex.Message);
            _logger.LogError($"Không thể kết nối đến Socket: {_webSocketUrl}, Lỗi: {ex.Message}");
        }
        // Đợi một khoảng thời gian trước khi thử lại kết nối
        await Task.Delay(_reconnect, stoppingToken);
    }

    private void Device_OnPacketArrival(object sender, PacketCapture e)
    {
        try
        {
            // Lấy dữ liệu gói tin
            var rawPacket = e.GetPacket();
            var packet = PacketDotNet.Packet.ParsePacket(rawPacket.LinkLayerType, rawPacket.Data);

            // Kiểm tra xem có phải là gói tin IP không
            var ipPacket = packet.PayloadPacket as IPv6Packet; //ethernetPacket
            if (ipPacket != null)
            {
                var tcpPacket = ipPacket.PayloadPacket as TcpPacket;

                if (tcpPacket != null)
                {
                    var payload = Encoding.ASCII.GetString(tcpPacket.PayloadData);
                    
                    // Kiểm tra nếu payload là HTTP GET hoặc các yêu cầu HTTP khác
                    if (Regex.IsMatch(payload, @"^(GET|POST|PUT|DELETE) "))
                    {
                        // Trích xuất phương thức và đường dẫn
                        var methodAndPath = Regex.Match(payload, @"^(GET|POST|PUT|DELETE) (/.*) HTTP");
                        var host = Regex.Match(payload, @"Host:\s*(\S+)");

                        if (methodAndPath.Success && host.Success)
                        {
                            //httpRequests.Add(new HttpRequestInfo
                            //{
                            //    SourceIp = ipPacket.SourceAddress.ToString(),
                            //    DestinationIp = ipPacket.DestinationAddress.ToString(),
                            //    SourcePort = tcpPacket.SourcePort,
                            //    DestinationPort = tcpPacket.DestinationPort,
                            //    SequenceNumber = tcpPacket.SequenceNumber,
                            //    MethodAndPath = methodAndPath.Value,
                            //    Time = (double)rawPacket.Timeval.Value
                            //});
                            // Gộp thành một dòng duy nhất:
                            string result = $"{methodAndPath.Groups[1].Value} {host.Groups[1].Value}{methodAndPath.Groups[2].Value} HTTP";
                            if (_requestCounts.ContainsKey(result))
                            {
                                _requestCounts[result]++;
                            }
                            else
                            {
                                _requestCounts[result] = 1;
                            }
                            //Console.WriteLine(result);
                        }
                    } 

                    // Kiểm tra nếu payload là HTTP Response
                    //if (Regex.IsMatch(payload, @"^HTTP/"))
                    //{                        
                    //    const int tolerance = 1000;
                    //    // Gói tin gửi đi, nên ngược
                    //    var matchingRequest = httpRequests.Where(req =>
                    //    req.SourceIp == ipPacket.DestinationAddress.ToString() &&
                    //    req.DestinationIp == ipPacket.SourceAddress.ToString() &&
                    //    req.SourcePort == tcpPacket.DestinationPort &&
                    //    req.DestinationPort == tcpPacket.SourcePort &&
                    //    (int)Math.Abs(tcpPacket.AcknowledgmentNumber - (req.SequenceNumber + tcpPacket.PayloadData.Length)) <= tolerance).FirstOrDefault();
                    //    // Trích xuất thông tin từ gói phản hồi
                    //    if (matchingRequest != null)
                    //    {
                    //        var responseTime = (double)((double)rawPacket.Timeval.Value - matchingRequest.Time);
                    //        ListResponseTime.Add(responseTime);
                    //        Console.WriteLine($"Response Time for request: {responseTime}");
                    //        httpRequests.Clear();
                    //    }
                    //}
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error while processing packet: {ex.Message}");
            _logger.LogError($"Lỗi khi bắt gói tin: {ex.Message}");
        }
    }

    public ICaptureDevice? GetAdapterWithKeyword()
    {
        // Lấy tất cả các adapter mạng
        var devices = CaptureDeviceList.Instance;
        if (devices.Count < 1)
        {
            Console.WriteLine("No devices found!");
            _logger.LogWarning("Không có Adapter mạng nào");
            return null;
        }
        // Lọc các adapter có chứa từ khóa trong tên hoặc mô tả
        var filteredDevices = devices.FirstOrDefault(d => d.Description != null && d.Description.ToLower().Contains(_adapter.ToLower()));

        if (filteredDevices != null)
        {
            Console.WriteLine($"Select a device containing the keyword '{_adapter}':");
            Console.WriteLine($"{filteredDevices.Description}");
            _logger.LogInformation($"Bắt API từ Adapter: {filteredDevices.Description}");
            return filteredDevices;
        }

        return null;
    }

    public static string GetLocalIpAddress()
    {
        string localIp = string.Empty; // Địa chỉ mặc định nếu không tìm thấy IPv4
        foreach (var networkInterface in Dns.GetHostEntry(Dns.GetHostName()).AddressList)
        {
            // Kiểm tra địa chỉ IPv4 không phải loopback
            if (networkInterface.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !IPAddress.IsLoopback(networkInterface))
            {
                localIp = networkInterface.ToString();
                break;
            }
        }
        return localIp;
    }

    public static List<(string, double, double)> GetDatabases(string connectionString)
    {
        var databases = new List<(string, double, double)>();
        string sqlQuery = @"
        CREATE TABLE #FileSize
        (
            dbName NVARCHAR(128), 
            type_desc NVARCHAR(128),
            CurrentSizeMB DECIMAL(10,2), 
            FreeSpaceMB DECIMAL(10,2)
        );
        INSERT INTO #FileSize (dbName, type_desc, CurrentSizeMB, FreeSpaceMB)
        EXEC sp_msforeachdb 
        '
        USE [?]; 
        IF DB_ID() > 4
        BEGIN
            SELECT 
                DB_NAME() AS DbName, 
                type_desc,
                size / 128.0 AS CurrentSizeMB,  
                CASE 
                    WHEN type = 0 THEN size / 128.0 - CAST(FILEPROPERTY(name, ''SpaceUsed'') AS INT) / 128.0 
                    ELSE 0 
                END AS FreeSpaceMB
            FROM sys.database_files
        END
        ';
        SELECT 
            dbName,
            SUM(CurrentSizeMB) AS TotalCurrentSizeMB,
            MAX(CASE WHEN type_desc = 'ROWS' THEN FreeSpaceMB ELSE 0 END) AS FreeSpaceMB 
        FROM #FileSize
        GROUP BY dbName;
        DROP TABLE #FileSize;";
        using (var connection = new SqlConnection(connectionString))
        {
            connection.Open();
            // Câu truy vấn SQL
            
            var command = new SqlCommand(sqlQuery, connection);

            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    var databaseName = reader.GetString(0);
                    var TotalcurrentSizeMB = (double)reader.GetDecimal(1);
                    var FreeSpaceMB = (double)reader.GetDecimal(2);
                    // Tạo dictionary cho mỗi database
                    var databaseInfo = (databaseName, TotalcurrentSizeMB, FreeSpaceMB);

                    databases.Add(databaseInfo);
                }
            }
        }
        return databases;
    }

    // Hàm trả về Dictionary chứa tốc độ gửi và nhận của tất cả các card mạng
    public Dictionary<string, (double receiveKbps, double sendKbps)> GetNetworkSpeed()
    {
        var networkSpeeds = new Dictionary<string, (double receiveKbps, double sendKbps)>();

        // Lấy tất cả các card mạng hoạt động
        var interfaces = NetworkInterface.GetAllNetworkInterfaces()
            .Where(ni => ni.OperationalStatus == OperationalStatus.Up &&
                         (ni.NetworkInterfaceType == NetworkInterfaceType.Ethernet ||
                          ni.NetworkInterfaceType == NetworkInterfaceType.Wireless80211))
            .ToList();

        if (!interfaces.Any())
        {
            Console.WriteLine("No active network interfaces found.");
            _logger.LogWarning("Không có card mạng hoạt động.");
            return networkSpeeds;
        }

        // Tính tổng tốc độ gửi và nhận cho tất cả các card mạng
        foreach (var ni in interfaces)
        {
            ManagementObjectSearcher searcher = new ManagementObjectSearcher("SELECT * FROM Win32_PerfFormattedData_Tcpip_NetworkInterface");

            foreach (var obj in searcher.Get())
            {
                var name = obj["Name"];
                var sentBytes = Convert.ToDouble(obj["BytesSentPerSec"]) / 1024;
                var receivedBytes = Convert.ToDouble(obj["BytesReceivedPerSec"]) / 1024;
                networkSpeeds[obj["Name"].ToString()] = (sentBytes, receivedBytes);
            }
        }

        // Trả về Dictionary chứa tốc độ của tất cả các card mạng
        return networkSpeeds;
    }

    // Lấy tổng RAM
    public double GetTotalRAM()
    {
        double totalRam = 0;

        using (var searcher = new ManagementObjectSearcher("SELECT TotalVisibleMemorySize FROM Win32_OperatingSystem"))
        {
            foreach (var obj in searcher.Get())
            {
                totalRam = Convert.ToDouble(obj["TotalVisibleMemorySize"]) / 1024; // Convert to MB
            }
        }

        return totalRam;
    }


    // FireWall
    //public static SystemSecurityInfo GetSecurityInfo()
    //{
    //    // Khởi tạo thông tin kết quả
    //    string firewallStatus = "inactive";
    //    int blockedAttempts = 15; // Giả sử số lần bị chặn cố gắng đăng nhập (có thể lấy từ cơ sở dữ liệu hoặc API khác)
    //    int successAttempts = 0;
    //    int failureAttempts = 0;

    //    try
    //    {
    //        // Lấy trạng thái tường lửa
    //        Process firewallProcess = new Process();
    //        firewallProcess.StartInfo.FileName = "netsh";
    //        firewallProcess.StartInfo.Arguments = "advfirewall show allprofiles";
    //        firewallProcess.StartInfo.RedirectStandardOutput = true;
    //        firewallProcess.StartInfo.UseShellExecute = false;
    //        firewallProcess.StartInfo.CreateNoWindow = true;
    //        firewallProcess.Start();
    //        string firewallOutput = firewallProcess.StandardOutput.ReadToEnd();
    //        firewallProcess.WaitForExit();

    //        if (firewallOutput.Contains("State                                 ON"))
    //        {
    //            firewallStatus = "active";
    //        }

    //        // Lấy thông tin đăng nhập từ Event Log
    //        EventLog eventLog = new EventLog("Security");

    //        foreach (EventLogEntry entry in eventLog.Entries.Cast<EventLogEntry>())
    //        {
    //            // Mã sự kiện đăng nhập thành công là 4624, thất bại là 4625
    //            if (entry.InstanceId == 4624) // Success
    //            {
    //                successAttempts++;
    //            }
    //            else if (entry.InstanceId == 4625) // Failure
    //            {
    //                failureAttempts++;
    //            }
    //        }
    //    }
    //    catch (Exception ex)
    //    {
    //        Console.WriteLine("Lỗi khi lấy thông tin bảo mật: " + ex.Message);
    //    }

    //    // Tạo và trả về thông tin bảo mật
    //    return new SystemSecurityInfo
    //    {
    //        FirewallStatus = firewallStatus,
    //        BlockedAttempts = blockedAttempts,
    //        LoginAttempts = new LoginAttemptInfo
    //        {
    //            Success = successAttempts,
    //            Failure = failureAttempts
    //        }
    //    };
    //}
}
