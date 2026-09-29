namespace VirtualPOSPrinter.Models;

public sealed record PrintJob(DateTime ReceivedAt, long SizeBytes, string SourceIp, string FilePath);

public enum ListenerState { Stopped, Listening, Refusing, Error }
