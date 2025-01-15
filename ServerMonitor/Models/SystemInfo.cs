namespace ServerMonitor.Models
{
    public class SystemInfo
    {
        public string ClientIp { get; set; }
        public DateTime DateStamp { get; set; }
        public float CpuUsage { get; set; }
        public float MemoryUsage { get; set; }
        public float StorageUsage { get; set; }
        public List<NetworkSpeedInfo> NetworkSpeed { get; set; }
        public Dictionary<string, int>? ApiStatistics { get; set; } // Chứa danh sách API và số lượng yêu cầu
        public List<string> ListDatabases { get; set; }
        public SystemSecurityInfo SystemSecurityInfo { get; set; }
    }
    public class NetworkSpeedInfo
    {
        public string NetworkInterface { get; set; }
        public string ReceiveSpeed { get; set; }
        public string SendSpeed { get; set; }
    }
    public class SystemSecurityInfo
    {
        public string FirewallStatus { get; set; }
        public int BlockedAttempts { get; set; }
        public LoginAttemptInfo LoginAttempts { get; set; }
    }

    public class LoginAttemptInfo
    {
        public int Success { get; set; }
        public int Failure { get; set; }
    }
}
