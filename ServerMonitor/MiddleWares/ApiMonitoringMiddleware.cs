namespace ServerMonitor.Middlewares
{
    public class ApiMonitoringMiddleware
    {
        private readonly RequestDelegate _next;
        private static readonly Dictionary<string, int> _currentApiCallCounts = new();
        private static readonly Dictionary<string, DateTime> _apiLastResetTime = new();
        private static readonly TimeSpan ResetInterval = TimeSpan.FromMinutes(1); // Reset mỗi 1 phút

        private static Timer _resetTimer;

        public ApiMonitoringMiddleware(RequestDelegate next)
        {
            _next = next;

            // Khởi tạo Timer để reset sau mỗi 1 phút
            if (_resetTimer == null)
            {
                _resetTimer = new Timer(ResetApiStats, null, ResetInterval, ResetInterval);
            }
        }

        public async Task InvokeAsync(HttpContext context)
        {
            var apiName = $"{context.Request.Method} {context.Request.Path}";

            // Đếm số lượng yêu cầu cho API đó (tại thời điểm hiện tại)
            lock (_currentApiCallCounts)
            {
                if (!_currentApiCallCounts.ContainsKey(apiName))
                {
                    _currentApiCallCounts[apiName] = 0;
                    _apiLastResetTime[apiName] = DateTime.Now;
                }

                _currentApiCallCounts[apiName]++;
            }

            // Tiến hành yêu cầu tiếp theo trong pipeline
            await _next(context);
        }

        // Phương thức Reset số liệu API
        private static void ResetApiStats(object state)
        {
            lock (_currentApiCallCounts)
            {
                var currentTime = DateTime.Now;

                // Kiểm tra và reset các API nếu đã đến thời gian reset
                foreach (var api in _currentApiCallCounts.Keys.ToList())
                {
                    // Nếu đã quá 1 phút kể từ lần reset cuối, reset số lượng yêu cầu
                    if ((currentTime - _apiLastResetTime[api]) >= ResetInterval)
                    {
                        _currentApiCallCounts[api] = 0;
                        _apiLastResetTime[api] = currentTime;
                    }
                }
            }
        }

        // Phương thức lấy số liệu thống kê API
        public static Dictionary<string, int> GetApiStatistics()
        {
            lock (_currentApiCallCounts)
            {
                return new Dictionary<string, int>(_currentApiCallCounts);
            }
        }
    }
}
