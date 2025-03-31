namespace ServerMonitor.Models
{
    public class CPUinfo
    {
        public double cpuUsage { get; set; }
        public float cpuTemperature { get; set; }
        public string? upTime { get; set; }
    }
}
