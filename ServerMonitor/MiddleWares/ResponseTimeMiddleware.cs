using System.Diagnostics;

public class ResponseTimeMiddleware
{
    private readonly RequestDelegate _next;
    private static List<double> _responseTimes = new List<double>(); // Lưu trữ thời gian phản hồi
    private static double _maxResponseTime = 0; // Thời gian phản hồi lớn nhất
    private static string _maxResponseTimeRequest = ""; // Lưu trữ thông tin yêu cầu có thời gian phản hồi lớn nhất
    private static DateTime _maxResponseTimeDate = DateTime.MinValue; // Lưu ngày giờ của yêu cầu có thời gian phản hồi lớn nhất

    private static Timer _timer; // Khai báo Timer
    public ResponseTimeMiddleware(RequestDelegate next)
    {
        _next = next;
        // Khởi tạo Timer để chạy mỗi 2 giây
        _timer = new Timer(CalculateResponseTimes, null, TimeSpan.Zero, TimeSpan.FromSeconds(2));
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var watch = Stopwatch.StartNew(); // Bắt đầu đếm thời gian

        await _next(context); // Tiến hành xử lý yêu cầu

        watch.Stop(); // Dừng đồng hồ khi phản hồi được gửi

        double responseTime = watch.Elapsed.TotalMilliseconds; // Thời gian phản hồi tính bằng mili giây

        // Lưu response time vào danh sách
        _responseTimes.Add(responseTime);

        // Kiểm tra xem response time có phải là lớn nhất không
        if (responseTime > _maxResponseTime)
        {
            _maxResponseTime = responseTime;
            _maxResponseTimeRequest = $"{context.Request.Method} {context.Request.Path}";
            _maxResponseTimeDate = DateTime.Now;
        }

        Console.WriteLine($"Response Time: {responseTime} ms"); // In thời gian phản hồi ra console (tuỳ chọn)
    }

    // Phương thức lấy Response Time trung bình, lớn nhất và yêu cầu có thời gian lớn nhất
    public static (double averageResponseTime, double maxResponseTime, string maxResponseRequest, DateTime maxResponseDate) GetResponseTimes()
    {
        if (_responseTimes.Count == 0)
            return (0, 0, "", DateTime.MinValue);

        double average = _responseTimes.Average();
        return (average, _maxResponseTime, _maxResponseTimeRequest, _maxResponseTimeDate);
    }
    private static void CalculateResponseTimes(object state)
    {
        if (_responseTimes.Count == 0)
            return;

        double average = _responseTimes.Average();

        Console.WriteLine($"Average Response Time: {average} ms");
        Console.WriteLine($"Max Response Time: {_maxResponseTime} ms");
        Console.WriteLine($"Max Response Request: {_maxResponseTimeRequest}");
        Console.WriteLine($"Max Response Date: {_maxResponseTimeDate}");
    }
}
