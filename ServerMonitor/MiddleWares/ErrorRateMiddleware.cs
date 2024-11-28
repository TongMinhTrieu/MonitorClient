namespace ServerMonitor.Middlewares
{
    public class ErrorRateMiddleware
    {
        private readonly RequestDelegate _next;
        private static int _totalRequests = 0;
        private static int _errorRequests = 0;

        public ErrorRateMiddleware(RequestDelegate next)
        {
            _next = next;
        }

        public async Task InvokeAsync(HttpContext context)
        {
            _totalRequests++;
            try
            {
                await _next(context);
                if (context.Response.StatusCode >= 400)
                    _errorRequests++;
            }
            catch
            {
                _errorRequests++;
                throw;
            }
        }

        public static float GetErrorRate()
        {
            return _totalRequests > 0 ? (_errorRequests / (float)_totalRequests) * 100 : 0;
        }
    }

}
