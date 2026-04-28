using Dapper.Contrib.Extensions;

namespace EnvDataCollector.Models
{
    [Table("opcua_server")]
    public class OpcUaServerEntity
    {
        [Key]   public int    Id             { get; set; }
                public string Name           { get; set; }
                public string EndpointUrl    { get; set; }
                public string SecurityMode   { get; set; } = "None";
                public string SecurityPolicy { get; set; } = "None";
                public string AuthType       { get; set; } = "Anonymous";
                public string Username       { get; set; }
                public string PasswordEnc    { get; set; }
                public int    Enabled        { get; set; } = 1;
                public string CreatedAt      { get; set; }
                public string UpdatedAt      { get; set; }
    }

    [Table("device")]
    public class DeviceEntity
    {
        [Key]   public int    Id         { get; set; }
                public string DeviceType { get; set; }
                public string DeviceCode { get; set; }
                public string DeviceName { get; set; }
                public int    ServerId   { get; set; }
                public int    Enabled    { get; set; } = 1;
                public string CreatedAt  { get; set; }
                public string UpdatedAt  { get; set; }
    }

    [Table("device_variable")]
    public class DeviceVariableEntity
    {
        [Key]   public int    Id          { get; set; }
                public int    DeviceId    { get; set; }
                public string VarRole     { get; set; }
                public string NodeId      { get; set; }
                public string DisplayName { get; set; }
                public string DataType    { get; set; }
                public int    SamplingMs  { get; set; } = 1000;
                public int    Enabled     { get; set; } = 1;
                public string CreatedAt   { get; set; }
                public string UpdatedAt   { get; set; }
    }

    [Table("camera_config")]
    public class CameraConfigEntity
    {
        [Key]   public int    Id             { get; set; }
                public int    DeviceId       { get; set; }
                public string Ip             { get; set; }
                public int    Port           { get; set; } = 8000;
                public string Username       { get; set; }
                public string PasswordEnc    { get; set; }
                public int    Channel        { get; set; } = 1;
                public int    Enabled        { get; set; } = 1;
                public int    MatchPreSec    { get; set; } = 30;
                public int    MatchPostSec   { get; set; } = 120;
                public string ImageStorePath { get; set; }
                public string ImageBaseUrl   { get; set; }
                public string CreatedAt      { get; set; }
                public string UpdatedAt      { get; set; }
    }

    [Table("plate_event")]
    public class PlateEventEntity
    {
        [Key]   public long    Id              { get; set; }
                public int     DeviceId        { get; set; }
                public string  PlateNo         { get; set; }
                public string  EventTime       { get; set; }
                public double? Confidence      { get; set; }
                // ★ 本地图片路径（相对于 exe 目录）
                public string  VehiclePicLocal { get; set; }
                public string  PlatePicLocal   { get; set; }
                // ★ 远程 URL（调用图片上传接口后回填）
                public string  VehiclePicUrl   { get; set; }
                public string  PlatePicUrl     { get; set; }
                public string  RawJson         { get; set; }
                public string  CreatedAt       { get; set; }
    }

    [Table("variable_trend")]
    public class VariableTrendEntity
    {
        [Key]   public long   Id         { get; set; }
                public int    DeviceId   { get; set; }
                public int    VariableId { get; set; }
                public string NodeId     { get; set; }
                public string VarRole    { get; set; }
                public string ValueStr   { get; set; }
                public string SourceTime { get; set; }
                public string CreatedAt  { get; set; }
    }

    [Table("device_snapshot")]
    public class DeviceSnapshotEntity
    {
        [Key]   public long    Id            { get; set; }
                public int     DeviceId      { get; set; }
                public string  Time          { get; set; }
                public int?    Online        { get; set; }
                public int?    Startup       { get; set; }
                public double? Currents      { get; set; }
                public double? WaterPressure { get; set; }
                public double? FlowQuantity  { get; set; }
                public string  PushStatus    { get; set; } = "Pending";
                public string  PushError     { get; set; }
                public string  CreatedAt     { get; set; }
    }

    [Table("run_record")]
    public class RunRecordEntity
    {
        [Key]   public long    Id            { get; set; }
                public int     DeviceId      { get; set; }
                public string  DeviceType    { get; set; }
                public string  DeviceCode    { get; set; }
                public string  StartTime     { get; set; }
                public string  EndTime       { get; set; }
                public int     RunTimeSec    { get; set; }
                public double? Currents      { get; set; }
                public double? WaterPressure { get; set; }
                public double? FlowQuantity  { get; set; }
                public double? CurrentsMax      { get; set; }
                public double? CurrentsMin      { get; set; }
                public double? CurrentsAvg      { get; set; }
                public double? CurrentsMedian   { get; set; }
                public double? WaterPressureMax    { get; set; }
                public double? WaterPressureMin    { get; set; }
                public double? WaterPressureAvg    { get; set; }
                public double? WaterPressureMedian { get; set; }
                public double? FlowQuantityMax     { get; set; }
                public double? FlowQuantityMin     { get; set; }
                public double? FlowQuantityAvg     { get; set; }
                public double? FlowQuantityMedian  { get; set; }
                public string  VehicleNo     { get; set; }
                // ★ 本地图片路径（留痕 + 补推时读取）
                public string  VehiclePicLocal   { get; set; }
                public string  VehicleNoPicLocal  { get; set; }
                // ★ 远程 URL（图片上传接口返回值）
                public string  VehiclePic    { get; set; }
                public string  VehicleNoPic  { get; set; }
                public string  CloseReason   { get; set; }
                public string  PushStatus    { get; set; } = "Pending";
                public string  PushError     { get; set; }
                public string  CreatedAt     { get; set; }
    }

    [Table("push_outbox")]
    public class OutboxMessageEntity
    {
        [Key]   public long   Id            { get; set; }
                public string MessageType   { get; set; }
                public string TargetUrl     { get; set; }
                public string PayloadJson   { get; set; }
                public string Status        { get; set; } = "Pending";
                public int    RetryCount    { get; set; }
                public int    MaxRetry      { get; set; } = 10;
                public string NextRetryTime { get; set; }
                public int?   LastHttpCode  { get; set; }
                public string LastError     { get; set; }
                public string RelatedTable  { get; set; }
                public long?  RelatedId     { get; set; }
                public string CreatedAt     { get; set; }
                public string UpdatedAt     { get; set; }
    }
}
