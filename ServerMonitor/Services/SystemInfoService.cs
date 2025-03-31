using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Net.WebSockets;
using System.Text;
using Microsoft.Data.SqlClient;
using System.Management;
using PacketDotNet;
using SharpPcap;
using System.Text.RegularExpressions;
using ServerMonitor.Models;
using LibreHardwareMonitor.Hardware;
using System.Text.Json;
using Microsoft.Web.Administration;
using System.Linq;
using System.Security.Cryptography;

namespace ServerMonitor.Services
{
    public partial class SystemInfoService : BackgroundService
    {
        private readonly string? _webSocketUrl; // Địa chỉ server WebSocket
        private ClientWebSocket _webSocketClient;
        private readonly string? _port;
        private readonly PerformanceCounter _cpuCounter;
        private readonly PerformanceCounter _ramCounter;
        private readonly int _reconnect; // Thời gian chờ giữa các lần thử kết nối lại
        private readonly int _period;   // Chu kỳ gửi
        private readonly string? _adapter;
        private readonly ILogger<SystemInfoService> _logger;
        private readonly List<ApiStatistics> _apistatistics = [];
        private readonly HttpClient _httpClient = new();

        private readonly string? _connectionstring;
        private readonly string? _testQuery;
        private readonly int _interval = 0;
        private readonly int _maxRetries = 0;
        private readonly List<PeakHour> _peakHours = [];

        private readonly Computer _computer;
        private readonly string clientIp = "0.0.0.0";

        private static volatile SQLMonitor sqlMonitor = new() { IsConnect = 0, ResponseTime = -1};

        public SystemInfoService(IConfiguration configuration, ILogger<SystemInfoService> logger)
        {
            _webSocketUrl = configuration["WebSocket:Url"];
            _webSocketClient = new ClientWebSocket();
            _port = configuration["WebSocket:Port"];
            _period = int.Parse(configuration["Time:period"] ?? "2000");
            _reconnect = int.Parse(configuration["Time:reconnect"] ?? "2000");
            _cpuCounter = new PerformanceCounter("Processor", "% Processor Time", "_Total");
            _ramCounter = new PerformanceCounter("Memory", "Available MBytes");
            _connectionstring = configuration["SQLMonitor:ConnectionStrings"];
            _testQuery = configuration["SQLMonitor:TestQuery"] ?? "SELECT TOP 1000 * FROM sys.objects ORDER BY create_date DESC;";
            _interval = int.Parse(configuration["SQLMonitor:CheckIntervalMinutes"] ?? "5") * 60;
            _maxRetries = int.Parse(configuration["SQLMonitor:MaxRetryAttempts"] ?? "5");
            _peakHours = configuration.GetSection("SQLMonitor:PeakHours").Get<List<PeakHour>>() ?? [];
            clientIp = GetLocalIpAddress();
            _adapter = configuration["Adapter:Name"];
            _logger = logger;


            // Khởi tạo đối tượng Computer và bật các cảm biến
            _computer = new Computer
            {
                IsCpuEnabled = true,
            };

            _computer.Open();
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {            
            bool isFirstrun = true;
            double count = _interval / 2;
            ICaptureDevice? selectedAdapter = GetAdapterWithKeyword();
            var totalMemory = GetTotalRAM();

            if (_connectionstring == null)
            {
                _logger.LogInformation("Không có chuỗi Connection. Sẽ không lấy các thông số SQL");
            }
            // Lưu trạng thái ổ đĩa trước đó

            List<(string, double, double)>? listDatabases = [("NoDatabase",0 ,0)];
            int numberOfSQLConnections = 0;

            while (!stoppingToken.IsCancellationRequested)
            {
              
                if (count + 10 > _interval / 2) //Chu kỳ lấy thông số SQL cấu hình trong appsetting
                {
                    try
                    {
                        if (_connectionstring != null)
                        {
                            if (isFirstrun || !IsPeakHour(_peakHours))
                            {
                                using (var connection = await TryGetSQLConnection(_connectionstring, _maxRetries))
                                {
                                    if (connection != null)
                                    {
                                        listDatabases = GetDatabases(connection);
                                        numberOfSQLConnections = GetSQLconnection(connection);
                                        int responseTime = await CheckSQLPerformance(connection, _testQuery);
                                        if (responseTime >= 0)
                                        {
                                            // Kết nối thành công - Truy vấn thành công
                                            sqlMonitor = new()
                                            {
                                                IsConnect = 1,
                                                ResponseTime = responseTime,
                                                IsSave = true
                                            };
                                        }
                                        else
                                        {
                                            // Kết nối thành công - Truy vấn thất bại
                                            sqlMonitor = new()
                                            {
                                                IsConnect = 1,
                                                ResponseTime = responseTime,
                                                IsSave = true
                                            };
                                        }
                                    }
                                    else
                                    {
                                        // Kết nối thất bại
                                        sqlMonitor = new()
                                        {
                                            IsConnect = 0,
                                            ResponseTime = 0,
                                            IsSave = true
                                        };
                                    }
                                }
                                count = 0;
                                isFirstrun = false;
                            }
                            else
                            {
                                // Giờ cao điểm, không check IsConnect = -1
                                sqlMonitor = new()
                                {
                                    IsConnect = -1,
                                    ResponseTime = 0,
                                    IsSave = true
                                };
                            }
                        }
                        else 
                        {
                            sqlMonitor = new()
                            {
                                IsConnect = -2,
                                ResponseTime = 0,
                                IsSave = false
                            };
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError($"Không thể Lấy thông số SQLMonitor. Lỗi: {ex.Message}");
                    }
                    
                }
                else { sqlMonitor.IsSave = false; }
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

                    // Lấy thông tin về các kết nối TCP hiện tại
                    IPGlobalProperties properties = IPGlobalProperties.GetIPGlobalProperties();
                    TcpConnectionInformation[] connections = properties.GetActiveTcpConnections();

                    // Tạo một PerformanceCounter để lấy số lượng kết nối HTTP của IIS
                    PerformanceCounter counter = new("Web Service", "Current Connections", "_Total");

                    // In ra số lượng kết nối HTTP hiện tại
                    //Console.WriteLine($"Số kết nối HTTP hiện tại: {counter.NextValue()}");

                    Random random = new();
                    // Lấy số kết nối
                    NumConnection connection = new()
                    {
                        //TCPconnect = 50,
                        //SQLconnect = 100,
                        //HTTPconnect = 150
                        TCPconnect = connections.Length,
                        SQLconnect = numberOfSQLConnections,
                        HTTPconnect = (int)counter.NextValue()
                    };

                    // Lấy nhiệt độ CPU
                    var cpu = _computer.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.Cpu);
                    float cpuTemperature = 0;
                    if (cpu != null)
                    {
                        cpu.Update();
                        // Lọc cảm biến nhiệt độ có tên chứa "CPU" hoặc "Package"(tùy theo phần cứng)
                        var tempSensor = cpu.Sensors.FirstOrDefault(sensor => sensor.SensorType == SensorType.Temperature &&
                                                                    sensor.Name.Contains("CPU Package"));

                        // Nếu tìm thấy cảm biến thích hợp, lấy giá trị nhiệt độ
                        if (tempSensor != null)
                        {
                            cpuTemperature = tempSensor.Value.GetValueOrDefault();
                        }
                    }
                    // Lấy thời gian hệ thống khởi động lại
                    TimeSpan uptime = TimeSpan.FromMilliseconds(Environment.TickCount);
                    string upTimestring = $"{uptime.Days:D3}:{uptime.Hours:D2}:{uptime.Minutes:D2}:{uptime.Seconds:D2}";

                    CPUinfo cPUinfo = new()
                    {
                        cpuUsage = Math.Round(_cpuCounter.NextValue(), 2),
                        cpuTemperature = cpuTemperature,
                        upTime = upTimestring
                        //cpuUsage = 50,
                        //cpuTemperature = 70,
                        //upTime = "10:10:10:10"
                    };
                    var memoryUsage = _ramCounter.NextValue();

                    // Lấy danh sách tất cả các ổ đĩa
                    var drives = DriveInfo.GetDrives();

                    List<(string, long, long)> driverInfo = [];

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
                    var dynamicDriverInfo = driverInfo.Select(info => new { DriverName = info.Item1, DiskFreeSpace = info.Item2, DiskTotalSpace = info.Item3}).ToList();

                    foreach (var item in dynamicDatabases)
                    {
                        if (item.DatabaseName == "NoDatabase") dynamicDatabases = [];
                    }

                    List<SiteInfo> siteInfo = [];
                    using (ServerManager serverManager = new ServerManager())
                    {
                        siteInfo = GetIISMetrics(serverManager);                      
                    }
                  
                    // Chuẩn bị dữ liệu JSON để gửi qua WebSocket
                    var systemInfo = new
                    {
                        ClientIp = clientIp,
                        DateStamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                        CPUinfo = cPUinfo,
                        MemoryAvailable = $"{memoryUsage:F2}",
                        MemoryTotal = $"{totalMemory:F2}",
                        DiskInfo = dynamicDriverInfo,
                        Connection = connection,
                        NetworkSpeed = networkSpeedFormatted,
                        ApiStatistics = _apistatistics,
                        ListDatabases = dynamicDatabases,
                        SiteInfo = siteInfo,
                        SqlMonitor = sqlMonitor
                    };

                    var jsonMessage = JsonSerializer.Serialize(systemInfo);

                    // Gửi dữ liệu qua WebSocket nếu kết nối mở
                    if (_webSocketClient != null)
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
                            //Console.WriteLine("Sent: " + jsonMessage);
                            //Console.WriteLine("Sent Data");
                            // Clear the byte array to help the garbage collector reclaim memory more quickly
                            Array.Clear(messageBytes, 0, messageBytes.Length);
                            _apistatistics.Clear();
                        }

                        else
                        {
                            Console.WriteLine("WebSocket is not connected.");
                            _logger.LogWarning("WebSocket mất kết nối");
                        }
                    }
                    else
                    {
                        Console.WriteLine("_webSocketClient null.");
                        _logger.LogWarning("_webSocketClient null");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine("Error: " + ex.Message);
                    _logger.LogError("Lỗi: {Message}", ex.Message);
                    // Nếu có lỗi, đợi một khoảng thời gian trước khi thử lại kết nối
                    await Task.Delay(_reconnect, stoppingToken);
                }
                finally
                {
                    GC.Collect(); // Thu gom bộ nhớ
                    GC.WaitForPendingFinalizers(); // Chờ các finalizer hoàn thành
                }
                await Task.Delay(_period, stoppingToken); // Đảm bảo client tiếp tục kiểm tra mỗi giây
            }
        }


        // Ping thử Check kết nối
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
                _logger.LogError("Ping lỗi: {Message}", ex.Message);
            }
            // Nếu có lỗi hoặc không nhận được phản hồi, coi như kết nối đã ngắt
            return false;
        }

        // Connect Web Socket
        private async Task ConnectWebSocketAsync(CancellationToken stoppingToken)
        {
            // Kết nối lại WebSocket khi không có kết nối
            try
            {
                string url = _webSocketUrl + ":" + _port;

                //Tạo dữ liệu JSON
                var requestBody = new
                {
                    Ip = clientIp,
                    Password = "Mercurian1104&Myrrh0505@131211#"
                };
                // Chuyển đổi thành JSON
                var json = JsonSerializer.Serialize(requestBody);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                //Gửi yêu cầu tới server
                var response = await _httpClient.PostAsync(url + "/servermonitor/api/authen/agent", content, stoppingToken);
                if (response.IsSuccessStatusCode)
                {
                    // Kiểm tra và đóng kết nối WebSocket nếu đang mở
                    if (_webSocketClient.State == WebSocketState.Open || _webSocketClient.State == WebSocketState.Connecting)
                    {
                        try
                        {
                            //Console.WriteLine("Đã đóng WebSocket trước khi kết nối lại.");
                            await _webSocketClient.CloseAsync(WebSocketCloseStatus.NormalClosure, "Reconnecting", stoppingToken);
                            _logger.LogInformation("Đã đóng WebSocket trước khi kết nối lại.");
                        }
                        catch (Exception ex)
                        {
                            //Console.WriteLine($"Lỗi khi đóng WebSocket: {ex.Message}. Hủy kết nối ngay lập tức.");
                            _logger.LogWarning($"Lỗi khi đóng WebSocket: {ex.Message}. Hủy kết nối ngay lập tức.");
                            _webSocketClient.Abort();
                        }
                    }
                    // Tạo WebSocket mới
                    _webSocketClient.Dispose();
                    _webSocketClient = new ClientWebSocket();

                    await _webSocketClient.ConnectAsync(new Uri(url.Replace("http", "ws") + "/ws"), stoppingToken);
                    Console.WriteLine("WebSocket connected to " + url);
                    _logger.LogInformation($"Kết nối đến Socket: {url}");
                }
                else
                {
                    _logger.LogError($"Không thể kết nối đến: {url}. Lỗi: {response.StatusCode}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("Error connecting to WebSocket: " + ex.Message);
                _logger.LogError($"Không thể kết nối đến Socket: {_webSocketUrl}, Lỗi: {ex.Message}");
            }
            // Đợi một khoảng thời gian trước khi thử lại kết nối
            await Task.Delay(_reconnect, stoppingToken);
        }

        // Hàm lấy PID từ cổng đích (dùng IPGlobalProperties)
        private static List<int> GetPidsFromPort(int port)
        {
            var pids = new List<int>();
            var listeners = IPGlobalProperties.GetIPGlobalProperties()
                .GetActiveTcpListeners()
                .Where(listener => listener.Port == port);

            foreach (var listener in listeners)
            {
                var ipAddress = listener.Address.ToString();
                var portNumber = listener.Port;
                int pid = GetPidFromConnection(ipAddress, portNumber);
                if (pid != -1 && !pids.Contains(pid)) // Tránh trùng lặp
                {
                    pids.Add(pid);
                }
            }
            return pids; // Trả về danh sách PID
        }

        private static int GetPidFromConnection(string ipAddress, int port)
        {
            var processStartInfo = new ProcessStartInfo
            {
                FileName = "netstat",
                Arguments = "-ano",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(processStartInfo);
            if (process == null)
            {
                // Log lỗi hoặc xử lý khi không thể khởi động netstat
                Console.WriteLine("Failed to start netstat process.");
                return -1;
            }
            using var reader = process.StandardOutput;
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                if (line.Contains(ipAddress) && line.Contains($":{port}"))
                {
                    var parts = line.Split([' '], StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length > 4 && int.TryParse(parts[4], out int pid))
                    {
                        return pid; // Vẫn trả về PID đầu tiên trong hàm này
                    }
                }
            }
            return -1;
        }

        // Hàm bắt API theo adapter
        private void Device_OnPacketArrival(object sender, PacketCapture e)
        {
            try
            {
                // Lấy dữ liệu gói tin
                var rawPacket = e.GetPacket();
                var packet = Packet.ParsePacket(rawPacket.LinkLayerType, rawPacket.Data);

                // Kiểm tra xem có phải là gói tin IP không
                //ethernetPacket
                if (packet.PayloadPacket is IPPacket ipPacket)
                {
                    string Direct = "In";
                    if (!ipPacket.DestinationAddress.Equals(clientIp))
                    {
                        Direct = "Out";
                    }
                    if (ipPacket.PayloadPacket is TcpPacket tcpPacket)
                    {
                        if (tcpPacket.PayloadData == null)
                        {
                            return; // Bỏ qua gói tin quá nhỏ
                        }
                        int destinationPort = tcpPacket.DestinationPort;
                        var payload = Encoding.ASCII.GetString(tcpPacket.PayloadData);
                        
                        var blockedDomains = new List<string>
                        {
                            "edgedl.me.gvt1.com",
                            "statsfe2.update.microsoft.com",
                            "ctldl.windowsupdate.com",
                            "localhost",
                            "c.pki.goog",
                            "x1.c.lencr.org",
                            "ocsp.digicert.com",
                            "msftconnecttest.com"
                        };

                        var logDomains = new List<string>
                        {
                            "xnxx.com",
                            "facebook.com",
                            "youtube.com",
                            "youporn.com",
                            "xvideos.com",
                            "torproject.org",
                            "tiktok.com",
                            "xhamster.com"
                        };

                        // Kiểm tra nếu payload là HTTP GET hoặc các yêu cầu HTTP khác
                        if (MyRegex2().IsMatch(payload))
                        {
                            // Trích xuất phương thức và đường dẫn
                            var methodAndPath = MyRegex().Match(payload);
                            var host = MyRegex1().Match(payload);

                            if (methodAndPath.Success && host.Success)
                            {
                                string extractedHost = host.Groups[1].Value;

                                // Kiểm tra nếu host có trong danh sách chặn thì bỏ qua
                                if (blockedDomains.Any(domain => extractedHost.Contains(domain)))
                                {
                                    return; // Bỏ qua gói tin này
                                }

                                string extractedPath = methodAndPath.Groups[2].Value;
                                var apiFullPath = extractedHost + extractedPath;
                                // **Loại bỏ các tham số trong URL** (chỉ lấy phần trước dấu `?`)
                                extractedPath = extractedPath.Split('?')[0];

                                List<int> pids = GetPidsFromPort(destinationPort);
                                if (logDomains.Any(domain => extractedHost.Contains(domain)))
                                {
                                    foreach (var pid in pids)
                                    {
                                        try
                                        {
                                            var process = Process.GetProcessById(pid);
                                            string processName = process.ProcessName;
                                            string executablePath = "N/A (System Process)";

                                            if (processName != "System" && pid != 4)
                                            {
                                                try
                                                {
                                                    executablePath = process.MainModule?.FileName ?? "MainModule is null";
                                                }
                                                catch (Exception moduleEx)
                                                {
                                                    _logger.LogWarning($"PID: {pid}, Cannot access MainModule: {moduleEx.Message}");
                                                    executablePath = "Unable to access";
                                                }
                                            }
                                            _logger.LogInformation($"PID: {pid}, Name: {processName}, Path: {executablePath}, Destination: {Direct}");
                                        }
                                        catch (Exception ex)
                                        {
                                            _logger.LogInformation($"PID: {pid} - Error: {ex.Message}");
                                        }
                                    }
                                   
                                    var refererMatch = MyRegex3().Match(payload);
                                    if (refererMatch.Success)
                                    {
                                        string referer = refererMatch.Groups[1].Value;
                                        _logger.LogInformation($"API: {apiFullPath}, Called by Referer: {referer}, PID: {pids.FirstOrDefault()}, Destination: {Direct}");
                                    }
                                    else if (pids.Contains(4))
                                    {
                                        _logger.LogInformation($"API: {apiFullPath}, Likely system-level request (PID 4), Destination: {Direct}");
                                    }
                                }

                                var apistatic = new ApiStatistics
                                {
                                    method = methodAndPath.Groups[1].Value,
                                    API = apiFullPath,
                                    calls = 1,
                                    Port = destinationPort, // Thêm cổng vào ApiStatistics
                                    Pids = pids,              // Thêm PID vào ApiStatistics
                                    Direct = Direct
                                };

                                // Kiểm tra xem apistatic đã có trong _apistatistics chưa
                                var existingItem = _apistatistics.FirstOrDefault(item => item.method == apistatic.method && item.API == apistatic.API);
                                if (existingItem != null)
                                {
                                    // Nếu đã có, tăng số lần gọi (calls)
                                    existingItem.calls++;
                                    existingItem.Port = apistatic.Port;
                                    existingItem.Pids = pids;
                                    existingItem.Direct = Direct;
                                }
                                else
                                {
                                    // Nếu chưa có, thêm mới vào danh sách
                                    _apistatistics.Add(apistatic);
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error while processing packet: {ex.Message}");
                _logger.LogError($"Lỗi khi bắt gói tin: {ex.Message}");
            }
        }

        // Lấy Adapter theo Keyword từ appsetting
        public ICaptureDevice? GetAdapterWithKeyword()
        {
            // Lấy tất cả các adapter mạng
            var devices = CaptureDeviceList.Instance;
            foreach (var item in devices)
            {
                _logger.LogInformation($"Adapter: {item.Description}");
            }
            if (devices.Count < 1)
            {
                Console.WriteLine("No devices found!");
                _logger.LogWarning("Không có Adapter mạng nào");
                return null;
            }

            if (_adapter == null) return null;
            // Lọc adapter khớp hoàn toàn trước, nếu không có thì lấy cái chứa từ khóa
            var filteredDevices = devices.FirstOrDefault(d => d.Description != null && d.Description.Equals(_adapter, StringComparison.CurrentCultureIgnoreCase))
                                ?? devices.FirstOrDefault(d => d.Description != null && d.Description.Contains(_adapter, StringComparison.CurrentCultureIgnoreCase));

            if (filteredDevices != null)
            {
                Console.WriteLine($"Select a device containing the keyword '{_adapter}':");
                Console.WriteLine($"{filteredDevices.Description}");
                _logger.LogInformation($"Bắt API từ Adapter: {filteredDevices.Description}");
                return filteredDevices;
            }

            return null;
        }

        // Lấy IP WAN
        public static string GetLocalIpAddress()
        {
            using HttpClient client = new();
            return client.GetStringAsync("https://ifconfig.me/ip").Result;
        }

        // Lấy Danh sách Database và Dung lượng
        public static List<(string, double, double)> GetDatabases(SqlConnection sqlConnection)
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
            WHERE dbName NOT IN ('DWConfiguration', 'DWDiagnostics', 'DWQueue', 'distribution')
            GROUP BY dbName;
            DROP TABLE #FileSize;";
            // Câu truy vấn SQL

            var command = new SqlCommand(sqlQuery, sqlConnection);

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
            return databases;
        }

        // Lấy số connection SQL
        public static int GetSQLconnection( SqlConnection sqlConnection)
        {
            string query = "SELECT COUNT(*) AS NumberOfConnections FROM sys.dm_exec_connections";
            SqlCommand command = new(query, sqlConnection);
            int numberOfConnections = (int)command.ExecuteScalar();

            return numberOfConnections;
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

            if (interfaces.Count == 0)
            {
                Console.WriteLine("No active network interfaces found.");
                _logger.LogWarning("Không có card mạng hoạt động.");
                return networkSpeeds;
            }

            // Tính tổng tốc độ gửi và nhận cho tất cả các card mạng
            foreach (var ni in interfaces)
            {
                ManagementObjectSearcher searcher = new("SELECT * FROM Win32_PerfFormattedData_Tcpip_NetworkInterface");

                foreach (var obj in searcher.Get())
                {
                    var name = obj["Name"]?.ToString() ?? "Unknown"; ;
                    var sentBytes = Convert.ToDouble(obj["BytesSentPerSec"]) / 1024;
                    var receivedBytes = Convert.ToDouble(obj["BytesReceivedPerSec"]) / 1024;
                    networkSpeeds[name] = (sentBytes, receivedBytes);
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

        // Lấy CPU, RAM, Connection từng Site trên IIS
        public List<SiteInfo> GetIISMetrics(ServerManager serverManager)
        {
            List<SiteInfo> ListInfo = [];
            foreach (var site in serverManager.Sites)
            {
                // Chỉ lấy site đang chạy
                if (site.State != ObjectState.Started)
                    continue;

                string siteName = site.Name;
                int processId = GetWorkerProcessId(serverManager, site.Id);
                if (processId == 0)
                {
                    continue;
                }

                try
                {
                    int connections = (int)GetCurrentConnections(siteName);
                    double cpuUsage = GetCPUUsage(processId);
                    double ramUsage = GetRAMUsage(processId);
                    SiteInfo siteInfo = new()
                    {
                        SiteName = siteName,
                        CPUPercent = Math.Round(cpuUsage,2),
                        Ram = Math.Round(ramUsage,2),
                        CurrentConnection = connections
                    };
                    ListInfo.Add(siteInfo);
                }
                catch (Exception ex)
                {
                    _logger.LogError($"Error retrieving metrics for {siteName}: {ex.Message}");
                }
            }
            return ListInfo;
        }

        // Lấy PID từng site IIS
        public int GetWorkerProcessId(ServerManager serverManager, long siteId)
        {
            var appPoolName = serverManager.Sites.FirstOrDefault(s => s.Id == siteId)?.Applications["/"]?.ApplicationPoolName;
            if (appPoolName == null)
            {
                return 0;
            }

            var workerProcesses = serverManager.WorkerProcesses;
            var process = workerProcesses.FirstOrDefault(wp => wp.AppPoolName == appPoolName);
            if (process == null)
            {
                return 0;
            }
            return process.ProcessId;
        }

        //public int GetWorkerProcessId(ServerManager serverManager, long siteId)
        //{
        //    var appPoolName = serverManager.Sites.FirstOrDefault(s => s.Id == siteId)?.Applications["/"]?.ApplicationPoolName;
        //    if (appPoolName == null) return 0;

        //    var workerProcesses = serverManager.WorkerProcesses;
        //    var process = workerProcesses.FirstOrDefault(wp => wp.AppPoolName == appPoolName);
        //    return process?.ProcessId ?? 0;
        //}

        // Lấy Connection từng site IIS
        public float GetCurrentConnections(string siteName)
        {
            try
            {
                using PerformanceCounter counter = new("Web Service", "Current Connections", siteName);
                return counter.NextValue();
            }
            catch
            {
                return 0;
            }
        }

        public readonly Dictionary<int, PerformanceCounter> cpuCounters = [];
        // Lấy CPU từng site IIS
        public double GetCPUUsage(int processId)
        {
            try
            {
                if (!cpuCounters.ContainsKey(processId))
                {
                    string instanceName = GetProcessInstanceName(processId);
                    cpuCounters[processId] = new PerformanceCounter("Process", "% Processor Time", instanceName, true);
                    cpuCounters[processId].NextValue(); // Lần đầu NextValue() luôn về 0
                }

                double cpuUsage = Math.Round(cpuCounters[processId].NextValue() / Environment.ProcessorCount, 2);
                return Math.Max(cpuUsage, 0.1f);
            }
            catch
            {
                return 0;
            }
        }

        // Lấy name theo PID từng site IIS
        public string GetProcessInstanceName(int processId)
        {
            string processName = Process.GetProcessById(processId).ProcessName; // Lấy tên tiến trình, ví dụ: "w3wp"

            // Lấy danh sách tất cả instance trong PerformanceCounter
            PerformanceCounterCategory category = new("Process");
            string[] instances = category.GetInstanceNames().Where(i => i.StartsWith(processName)).ToArray();

            foreach (string instance in instances)
            {
                try
                {
                    using (PerformanceCounter counter = new("Process", "ID Process", instance, true))
                    {
                        if ((int)counter.NextValue() == processId)
                            return instance; // Trả về đúng instance name
                    };
                }
                catch
                {
                    continue;
                }
            }

            return processName; // Trả về mặc định nếu không tìm thấy
        }

        public readonly Dictionary<int, PerformanceCounter> ramCounters = [];

        // Lấy Ram từng site IIS
        public double GetRAMUsage(int processId)
        {
            try
            {
                if (!ramCounters.ContainsKey(processId))
                {
                    string instanceName = GetProcessInstanceName(processId);
                    ramCounters[processId] = new PerformanceCounter("Process", "Working Set", instanceName, true);
                }
                return Math.Round(ramCounters[processId].NextValue() / (1024 * 1024), 2);
            }
            catch
            {
                return 0;
            }
        }

        // Hàm không check kết nối trong giờ cao điểm (Thời gian từ Server trả về qua hàm Post xác thực)
        public static bool IsPeakHour(List<PeakHour> peakHours)
        {
            TimeSpan now = DateTime.Now.TimeOfDay;
            foreach (var period in peakHours)
            {
                if (TimeSpan.TryParse(period.Start, out var start) && TimeSpan.TryParse(period.End, out var end))
                {
                    if (now >= start && now <= end) return true;
                }
            }
            return false;
        }

        // Hàm check connect SQL
        public async Task<SqlConnection?> TryGetSQLConnection(string connectionString, int maxRetries)
        {
            int retryCount = 0;
            while (retryCount < maxRetries)
            {
                try
                {
                    var connection = new SqlConnection(connectionString);
                    await connection.OpenAsync();
                    //Console.WriteLine($"Ket noi SQL thanh cong - {DateTime.Now:HH:mm}");
                    return connection;
                }
                catch (Exception ex)
                {
                    retryCount++;
                    _logger.LogInformation($"Loi ket noi SQL (lan {retryCount}/{maxRetries}): {ex.Message}");
                    if (retryCount >= maxRetries)
                    {
                        _logger.LogError($"Canh bao: Khong the ket noi SQL sau {maxRetries} lan thu!");
                        return null;
                    }

                    await Task.Delay(2000); // Chờ 5 giây trước khi thử lại
                }
            }
            return null;
        }

        // Hàm check thời gian phản hồi câu Query thử
        public async Task<int> CheckSQLPerformance(SqlConnection connection, string? query)
        {
            try
            {
                var stopwatch = Stopwatch.StartNew();
                using (var command = new SqlCommand(query, connection))
                {
                    await command.ExecuteScalarAsync();
                }
                //Thread.Sleep(4000); Test thời giam chậm
                stopwatch.Stop();

                Console.WriteLine($"SQL Response Time: {stopwatch.ElapsedMilliseconds} ms - {DateTime.Now:HH:mm}");
                return (int)stopwatch.ElapsedMilliseconds;
            }
            catch (Exception ex)
            {
                _logger.LogError($"SQL Check Failed: {ex.Message}");
                return -1; // Trả về -1 nếu lỗi
            }
        }

        [GeneratedRegex(@"^(GET|POST|PUT|DELETE) (/.*) HTTP")]
        private static partial Regex MyRegex();
        [GeneratedRegex(@"Host:\s*(\S+)")]
        private static partial Regex MyRegex1();
        [GeneratedRegex(@"^(GET|POST|PUT|DELETE) ")]
        private static partial Regex MyRegex2();
        [GeneratedRegex(@"Referer:\s*([^\r\n]+)", RegexOptions.IgnoreCase, "en-US")]
        private static partial Regex MyRegex3();
    }
}