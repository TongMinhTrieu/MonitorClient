namespace ServerMonitor.Models
{
    public class SiteInfo
    {
        public string SiteName { get; set; }
        public double CPUPercent { get; set; }
        public double Ram { get; set; }
        public int CurrentConnection { get; set; }
    }
}
