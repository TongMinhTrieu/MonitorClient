namespace ServerMonitor.Models
{
    public class TcpCheck
    {
        public bool IsCheck { get; set; }
        public Dictionary<string, string> ListCheck { get; set; } = [];
    }

    public class Result
    {
        public string ServerIp { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty;
        public List<ListResult>? ListResults { get; set; }
    }
    public class  ListResult
    {
        public string HostName {  get; set; } = string.Empty;
        public string UrlCheck { get; set; } = string.Empty;
        public bool IsConnect { get; set; } = false;
    }
}
