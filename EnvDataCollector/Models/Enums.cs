namespace EnvDataCollector.Models
{
    public enum PushStatus    { Pending, Success, Failed }
    public enum CloseReason   { Normal, Timeout, Forced }
    public enum HeartbeatMode { Bit, Counter }
    public enum RetryBackoff  { Fixed, Exponential }
    public enum VarRole
    {
        Startup,
        WaterPressure,
        Currents,
        FlowQuantity,
        RunStatus,
        AlarmBit
    }
}
