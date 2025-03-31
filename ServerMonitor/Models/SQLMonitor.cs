namespace ServerMonitor.Models
{
    public class SQLMonitor
    {
        public int IsConnect { get; set; }
        public int ResponseTime { get; set; } // Milisecond
        public bool IsSave  { get; set; }
    }
}
