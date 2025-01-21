
public class HttpRequestInfo
{
    public string? SourceIp { get; set; }
    public string? DestinationIp { get; set; }
    public ushort SourcePort { get; set; }
    public ushort DestinationPort { get; set; }
    public uint SequenceNumber { get; set; }
    public string? MethodAndPath { get; set; }
    public double? Time { get; set; }
}

