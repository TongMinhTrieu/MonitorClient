using ServerMonitor.Middlewares;
using ServerMonitor.Models;
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
using PcapDotNet.Packets;
using System.Text.RegularExpressions;
using static System.Net.Mime.MediaTypeNames;
using static System.Net.WebRequestMethods;


public class SystemInfoService : BackgroundService
{
    private readonly string _webSocketUrl; // Địa chỉ server WebSocket
    private ClientWebSocket _webSocketClient;
    private readonly List<string> listDatabases;

    private readonly PerformanceCounter _cpuCounter;
    private readonly PerformanceCounter _ramCounter;
    private readonly int _reconnect; // Thời gian chờ giữa các lần thử kết nối lại
    private readonly int _period;   // Chu kỳ gửi

    private Dictionary<string, int> _requestCounts = new Dictionary<string, int>();

    private readonly List<string> _listAPI = new List<string>();

    public SystemInfoService(IConfiguration configuration)
    {
        _webSocketUrl = configuration["WebSocket:Url"];
        _period = int.Parse(configuration["Time:period"]);
        _reconnect = int.Parse(configuration["Time:reconnect"]);
        _cpuCounter = new PerformanceCounter("Processor", "% Processor Time", "_Total");
        _ramCounter = new PerformanceCounter("Memory", "Available MBytes");
        listDatabases = GetDatabases(configuration["ConnectionStrings:Default"]);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Lấy danh sách các adapter mạng
        var devices = CaptureDeviceList.Instance;
        if (devices.Count < 1)
        {
            Console.WriteLine("No devices found!");
            return;
        }

        // Chọn adapter đầu tiên (có thể thay đổi logic tùy nhu cầu)
        ICaptureDevice selectedAdapter = GetAdapterWithKeyword();
        
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
                }

                // Lấy địa chỉ IP của client
                string clientIp = GetLocalIpAddress();

                // Tạo dữ liệu giám sát
                var cpuUsage = _cpuCounter.NextValue();
                var memoryUsage = _ramCounter.NextValue();

                var driveInfo = new DriveInfo("C");
                var freeSpace = driveInfo.AvailableFreeSpace / (1024 * 1024 * 1024);
                var totalSpace = driveInfo.TotalSize / (1024 * 1024 * 1024);

                var networkStats = GetNetworkSpeed();
                // Chuyển đổi dictionary thành danh sách (hoặc một chuỗi, nếu cần)
                var networkSpeedFormatted = networkStats.Select(ni => new
                {
                    NetworkInterface = ni.Key,
                    ReceiveSpeed = $"{ni.Value.receiveKbps:F2} Kbps",
                    SendSpeed = $"{ni.Value.sendKbps:F2} Kbps"
                }).ToList();
                
                // Chuẩn bị dữ liệu JSON để gửi qua WebSocket
                var systemInfo = new
                {
                    ClientIp = clientIp,
                    DateStamp = DateTime.Now,
                    CpuUsage = $"{cpuUsage:F2}",
                    MemoryAvailable = $"{memoryUsage:F2}",
                    DiskFreeSpace = $"{freeSpace:F2}",
                    DiskTotalSpace = $"{totalSpace:F2}",
                    NetworkSpeed = networkSpeedFormatted,
                    ApiStatistics = _requestCounts,
                    ListDatabases = listDatabases
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
                    _requestCounts.Clear();
                }
                
                else
                {
                    Console.WriteLine("WebSocket is not connected.");
                }

            }
            catch (Exception ex)
            {
                Console.WriteLine("Error: " + ex.Message);
                // Nếu có lỗi, đợi một khoảng thời gian trước khi thử lại kết nối
                await Task.Delay(_reconnect, stoppingToken);
            }
            
            await Task.Delay(5000, stoppingToken); // Đảm bảo client tiếp tục kiểm tra mỗi giây
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
                        var methodAndPath = Regex.Match(payload, @"^(GET|POST|PUT|DELETE) (/.*) HTTP/1.1");
                        var host = Regex.Match(payload, @"Host:\s*(\S+)");

                        if (methodAndPath.Success && host.Success)
                        {
                            // Gộp thành một dòng duy nhất:
                            string result = $"{methodAndPath.Groups[1].Value} {host.Groups[1].Value}{methodAndPath.Groups[2].Value} HTTP/1.1";
                            if (_requestCounts.ContainsKey(result))
                            {
                                _requestCounts[result]++;
                            }
                            else
                            {
                                _requestCounts[result] = 1;
                            }
                            // Thêm kết quả vào list
                        }
                    }
                }
            }

        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error while processing packet: {ex.Message}");
        }
    }

    public ICaptureDevice GetAdapterWithKeyword()
    {
        // Lấy tất cả các adapter mạng
        var devices = CaptureDeviceList.Instance;
        if (devices.Count < 1)
        {
            Console.WriteLine("No devices found!");
            return null;
        }
        string keyword = "Adapter for loopback traffic capture";
        // Lọc các adapter có chứa từ khóa trong tên hoặc mô tả
        var filteredDevices = devices.FirstOrDefault(d => d.Description != null && d.Description.ToLower().Contains(keyword.ToLower()));

        if (filteredDevices != null)
        {
            Console.WriteLine($"Select a device containing the keyword '{keyword}':");
            Console.WriteLine($" {filteredDevices.Description}");

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

    public static List<string> GetDatabases(string connectionString)
    {
        var databases = new List<string>();
        using (var connection = new SqlConnection(connectionString))
        {
            connection.Open();
            var command = new SqlCommand("SELECT name FROM sys.databases WHERE state = 0", connection); // state = 0: Online

            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    databases.Add(reader.GetString(0));
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
