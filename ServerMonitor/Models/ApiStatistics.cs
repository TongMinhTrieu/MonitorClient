namespace ServerMonitor.Models
{
    public class ApiStatistics
    {
        public string method { get; set; } = "";
        public string API { get; set; } = "";
        public int calls { get; set; }
        public int Port { get; set; }
        public List<int> Pids { get; set; } = [];
        public string Direct { get; set; } = "";
    }
}
