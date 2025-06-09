using System.Diagnostics;
using System.Text.Json;
using System.Text;
using LogLevel = ServerMonitor.Enum.LogLevel;
using ServerMonitor.Models;

namespace ServerMonitor.Services
{
    public class TcpCheckService : BackgroundService
    {
        private readonly ILogService logService;
        private readonly string caller;
        private readonly TimeSpan checkInterval;
        private readonly HttpClient httpClient;
        private readonly string apiUrl;
        private readonly string password;
        private readonly string clientIp;
        private readonly TcpCheck config;

        public TcpCheckService(ILogService logService, IConfiguration configuration, HttpClient httpClient)
        {
            this.logService = logService;
            this.httpClient = httpClient;
            caller = ((GetType().Namespace?.Split('.') ?? []).LastOrDefault() + "." ?? "Unknown.") + GetType().Name;
            password = configuration["AgentPassword"] ?? "Mercurian1104&Myrrh0505@131211#";
            checkInterval = TimeSpan.FromMinutes(int.Parse(configuration["Time:interval"] ?? "3"));
            apiUrl = configuration["WebSocket:Url"] + ":" + configuration["WebSocket:Port"] ?? "https://monitor.adgps.vn:9119";
            config = configuration.GetSection("TcpCheck").Get<TcpCheck>() ?? new TcpCheck { IsCheck = false, ListCheck = [] };
            clientIp = GetLocalIpAddress();
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            string message;
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    if (config.IsCheck && config.ListCheck.Count != 0)
                    {
                        List<ListResult> listResults = [];
                        foreach (var check in config.ListCheck)
                        {
                            if (TryParseIpPort(check.Value, out string ip, out int port))
                            {
                                var (Success, Details) = await RunTestNetConnection(ip, port);
                                listResults.Add(new ListResult
                                {
                                    HostName = check.Key,
                                    UrlCheck = $"{ip}:{port}",
                                    IsConnect = Success
                                });
                            }
                            else
                            {
                                message = $"Invalid IP:Port format: {check.Value}";
                                logService.Logging(LogLevel.Warning, message, caller);
                            }
                        }

                        await SendResultsAsync(clientIp, password, listResults, stoppingToken);
                    }
                    else
                    {
                        message = "No checks performed: IsCheck is false or ListCheck is empty.";
                        logService.Logging(LogLevel.Information, message, caller);
                    }
                }
                catch (Exception ex)
                {
                    message = $"Error in TcpCheckService: {ex.Message}";
                    logService.Logging(LogLevel.Error, message, caller);
                }

                await Task.Delay(checkInterval, stoppingToken);
            }
        }

        private async Task SendResultsAsync(string serverIp, string password, List<ListResult> results, CancellationToken stoppingToken)
        {
            string sendResultUrl = apiUrl + "/servermonitor/api/tcpconnect/clientsendresult";
            try
            {
                var resultPayload = new Result
                {
                    ServerIp = serverIp,
                    Password = password,
                    ListResults = results
                };
                var content = new StringContent(JsonSerializer.Serialize(resultPayload), Encoding.UTF8, "application/json");

                var response = await httpClient.PostAsync(sendResultUrl, content, stoppingToken);
                response.EnsureSuccessStatusCode();
            }
            catch (Exception ex)
            {
                string message = $"Failed to send results to {sendResultUrl}: {ex.Message}";
                logService.Logging(LogLevel.Error, message, caller);
            }
        }

        private static bool TryParseIpPort(string check, out string ip, out int port)
        {
            ip = string.Empty;
            port = 0;
            if (string.IsNullOrEmpty(check)) return false;

            var parts = check.Split(':');
            if (parts.Length != 2 || !int.TryParse(parts[1], out port))
            {
                return false;
            }

            ip = parts[0].Trim();
            return !string.IsNullOrEmpty(ip);
        }

        private static async Task<(bool Success, string Details)> RunTestNetConnection(string computerName, int port)
        {
            try
            {
                using var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "powershell.exe",
                        Arguments = $"-Command Test-NetConnection -ComputerName {computerName} -Port {port}",
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    }
                };

                process.Start();
                string output = await process.StandardOutput.ReadToEndAsync();
                string error = await process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync();

                bool success = GetTcpTestSucceeded(output);
                string details = string.IsNullOrEmpty(error) ? output : error;

                return (success, details);
            }
            catch (Exception ex)
            {
                return (false, $"Exception: {ex.Message}");
            }
        }

        private static bool GetTcpTestSucceeded(string input)
        {
            var lines = input.Split(["\n"], StringSplitOptions.RemoveEmptyEntries);
            foreach (var line in lines)
            {
                if (line.Contains("TcpTestSucceeded"))
                {
                    var value = line.Split(':')[1].Trim();
                    return bool.Parse(value);
                }
            }
            return false;
        }

        public static string GetLocalIpAddress()
        {
            using HttpClient client = new();
            try
            {
                return client.GetStringAsync("https://ifconfig.me/ip").Result;
            }
            catch
            {
                return client.GetStringAsync("https://api.ipify.org/").Result;
            }
        }
    }
}