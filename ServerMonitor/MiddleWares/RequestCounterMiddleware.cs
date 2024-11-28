namespace ServerMonitor.Middlewares
{
    public class RequestCounterMiddleware
    {
        private readonly RequestDelegate _next;
        private static int _requestCount = 0;

        public RequestCounterMiddleware(RequestDelegate next)
        {
            _next = next;
        }

        public async Task InvokeAsync(HttpContext context)
        {
            _requestCount++;
            await _next(context);
        }

        public static int GetRequestsPerSecond()
        {
            return _requestCount;
        }

        public static void ResetCounter()
        {
            _requestCount = 0;
        }
    }

}
