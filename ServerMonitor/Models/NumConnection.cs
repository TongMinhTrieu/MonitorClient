namespace ServerMonitor.Models
{
    public class NumConnection
    {
        public int TCPconnect {  get; set; }
        public int SQLconnect { get; set; }
        public int HTTPconnect { get; set; }
    }
}
