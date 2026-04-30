using Newtonsoft.Json;

namespace EnvDataCollector.Models
{
    public class DeviceStatusDto
    {
        [JsonProperty("Time")]          public string  Time          { get; set; }
        [JsonProperty("DeviceType")]    public string  DeviceType    { get; set; }
        [JsonProperty("DeviceCode")]    public string  DeviceCode    { get; set; }
        [JsonProperty("OnLine")]        public int?    OnLine        { get; set; }
        [JsonProperty("Startup")]       public int?    Startup       { get; set; }
        [JsonProperty("Currents")]      public double? Currents      { get; set; }
        [JsonProperty("WaterPressure")] public double? WaterPressure { get; set; }
        [JsonProperty("FlowQuantity")]  public double? FlowQuantity  { get; set; }
    }

    public class DeviceEventDto
    {
        [JsonProperty("Time")]          public string  Time          { get; set; }
        [JsonProperty("DeviceType")]    public string  DeviceType    { get; set; }
        [JsonProperty("DeviceCode")]    public string  DeviceCode    { get; set; }
        [JsonProperty("StartTime")]     public string  StartTime     { get; set; }
        [JsonProperty("EndTime")]       public string  EndTime       { get; set; }
        [JsonProperty("RunTime")]       public long    RunTime       { get; set; }

        // ★ 电流统计
        [JsonProperty("CurrentsAvg")]      public double? CurrentsAvg      { get; set; }
        [JsonProperty("CurrentsMax")]      public double? CurrentsMax      { get; set; }
        [JsonProperty("CurrentsMin")]      public double? CurrentsMin      { get; set; }
        [JsonProperty("CurrentsMedian")]   public double? CurrentsMedian   { get; set; }
        // ★ 水压统计
        [JsonProperty("WaterPressureAvg")]    public double? WaterPressureAvg    { get; set; }
        [JsonProperty("WaterPressureMax")]    public double? WaterPressureMax    { get; set; }
        [JsonProperty("WaterPressureMin")]    public double? WaterPressureMin    { get; set; }
        [JsonProperty("WaterPressureMedian")] public double? WaterPressureMedian { get; set; }
        // ★ 流量统计
        [JsonProperty("FlowQuantityAvg")]     public double? FlowQuantityAvg     { get; set; }
        [JsonProperty("FlowQuantityMax")]     public double? FlowQuantityMax     { get; set; }
        [JsonProperty("FlowQuantityMin")]     public double? FlowQuantityMin     { get; set; }
        [JsonProperty("FlowQuantityMedian")]  public double? FlowQuantityMedian  { get; set; }

        // 向后兼容：保留原字段名，值 = Avg
        [JsonProperty("Currents")]      public double? Currents      { get; set; }
        [JsonProperty("WaterPressure")] public double? WaterPressure { get; set; }
        [JsonProperty("FlowQuantity")]  public double? FlowQuantity  { get; set; }

        [JsonProperty("VehicleNo")]     public string  VehicleNo     { get; set; }
        [JsonProperty("VehiclePic")]    public string  VehiclePic    { get; set; }
        [JsonProperty("VehicleNoPic")]  public string  VehicleNoPic  { get; set; }
    }

    public class TokenRequest
    {
        [JsonProperty("username")] public string Username { get; set; }
        [JsonProperty("password")] public string Password { get; set; }
    }

    public class TokenResponse
    {
        [JsonProperty("token")]     public string Token     { get; set; }
        [JsonProperty("expiresIn")] public int    ExpiresIn { get; set; } = 3600;
    }

    public class PushApiResponse
    {
        [JsonProperty("code")]    public int    Code    { get; set; }
        [JsonProperty("message")] public string Message { get; set; }
        public bool IsSuccess => Code == 0 || Code == 200;
    }

    public class OpcNodeInfo
    {
        public string NodeId      { get; set; }
        public string DisplayName { get; set; }
        public string BrowseName  { get; set; }
        public string DataType    { get; set; }
        public string Value       { get; set; }
        public string BrowsePath  { get; set; }
        public bool   IsVariable  { get; set; }
    }

    public class MonitorSnapshot
    {
        public int  OpcUaDisconnectedCount { get; set; }
        public int  CameraOfflineCount     { get; set; }
        public int  PushFailedCount        { get; set; }
        public int  PushPendingCount       { get; set; }
        public int  PushOldestPendingMin   { get; set; }
        public bool OpcUaAnyDisconnected   => OpcUaDisconnectedCount > 0;
        public bool CameraAnyOffline       => CameraOfflineCount > 0;
        public bool PushHasFailed          => PushFailedCount > 0;
        public bool PushHasPending         => PushPendingCount > 0;
    }
}
