using System.Diagnostics;

namespace ServerMonitor.Middlewares
{
    public class ResponseTimeMiddleware
    {
        private readonly RequestDelegate _next;
        public static List<float> ResponseTimes { get; private set; } = new();

        public ResponseTimeMiddleware(RequestDelegate next)
        {
            _next = next;
        }

        public async Task InvokeAsync(HttpContext context)
        {
            var stopwatch = Stopwatch.StartNew();
            await _next(context);
            stopwatch.Stop();

            ResponseTimes.Add(stopwatch.ElapsedMilliseconds);
            if (ResponseTimes.Count > 100) // Giới hạn số lượng mẫu
                ResponseTimes.RemoveAt(0);
        }
    }

}
