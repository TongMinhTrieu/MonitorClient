namespace ServerMonitor.Models
{
    public class SystemInfo
    {
        public string ClientIp { get; set; }
        public float CpuUsage { get; set; }
        public float MemoryUsage { get; set; }
        public float StorageUsage { get; set; }
        public float NetworkSpeed { get; set; }
        public List<object> ApiStatistics { get; set; } // Chứa danh sách API và số lượng yêu cầu
        public DateTime DateStamp { get; set; }
    }
}
