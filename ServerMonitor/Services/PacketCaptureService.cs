using Microsoft.Extensions.Hosting;
using PacketDotNet;
using PcapDotNet.Core;
using PcapDotNet.Packets;
using SharpPcap;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

public class PacketCaptureService : IHostedService
{
    private readonly List<string> _listAPI;
    private readonly Timer _resetTimer;

    public PacketCaptureService()
    {
        _listAPI = new List<string>();
    }

    public List<string> GetCapturedRequests()
    {
        return new List<string>(_listAPI);  // Trả về bản sao của list để bảo vệ dữ liệu gốc
    }

    // Lệnh bắt gói tin trong background task
    public Task StartAsync(CancellationToken cancellationToken)
    {
        Task.Run(() => StartPacketCapture(), cancellationToken);
        return Task.CompletedTask;
    }

    // Dừng việc bắt gói tin khi ứng dụng dừng
    public Task StopAsync(CancellationToken cancellationToken)
    {
        // Bạn có thể thêm logic dừng các công việc đang chạy ở đây nếu cần
        return Task.CompletedTask;
    }

    private void StartPacketCapture()
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

        // Tạo một luồng riêng để bắt gói tin
        if (selectedAdapter != null)
        {
            Console.WriteLine($"You selected: {selectedAdapter.Description}");

            // Sau đó có thể mở và sử dụng adapter này
            selectedAdapter.Open();
            Console.WriteLine("Adapter is ready for capturing packets.");

            // Nếu cần bắt gói tin
            selectedAdapter.OnPacketArrival += new PacketArrivalEventHandler(Device_OnPacketArrival);

            Console.WriteLine("Capturing HTTP packets...");
            selectedAdapter.StartCapture();
        }
        else
        {
            Console.WriteLine("No suitable adapter found.");
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
            var ipPacket = packet.PayloadPacket as IPPacket; //ethernetPacket
            if (ipPacket != null)
            {
                var tcpPacket = ipPacket.PayloadPacket as TcpPacket;
                // Chỉ lấy gói tin HTTP (cổng 80)
                if (tcpPacket != null)
                {
                    var payload = Encoding.ASCII.GetString(tcpPacket.PayloadData);

                    // Kiểm tra nếu payload là HTTP GET hoặc các yêu cầu HTTP khác
                    if (Regex.IsMatch(payload, @"^(GET|POST|PUT|DELETE|HEAD|OPTIONS|TRACE|CONNECT) "))
                    {
                        // Trích xuất phương thức và đường dẫn
                        var methodAndPath = Regex.Match(payload, @"^(GET|POST|PUT|DELETE|HEAD|OPTIONS|TRACE|CONNECT) (/.*) HTTP/1.1");
                        var host = Regex.Match(payload, @"Host:\s*(\S+)");

                        if (methodAndPath.Success && host.Success)
                        {
                            // Gộp thành một dòng duy nhất: GET localhost:2112/api/test HTTP/1.1
                            string result = $"{methodAndPath.Groups[1].Value} {host.Groups[1].Value}{methodAndPath.Groups[2].Value} HTTP/1.1";
                            // Thêm kết quả vào list
                            _listAPI.Add(result);
                            Console.WriteLine(result);
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

    public void ResetList()
    {
        _listAPI.Clear();
    }

    
}
