using System.IO.Pipes;

namespace TabletMonitor;

/// <summary>Brings the running instance to front when the exe is started again.</summary>
internal static class SingleInstance
{
    private const string PipeName = "TabletMonitor.Activate";

    public static void SignalExisting()
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(1000);
            client.WriteByte(1);
        }
        catch
        {
            // The other instance is shutting down.
        }
    }

    public static void Listen(Action onActivate)
    {
        _ = Task.Run(async () =>
        {
            while (true)
            {
                try
                {
                    await using var server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                    await server.WaitForConnectionAsync();
                    if (server.ReadByte() == 1)
                        onActivate();
                }
                catch (Exception ex)
                {
                    Log.Warn($"Single instance pipe: {ex.Message}");
                    await Task.Delay(1000);
                }
            }
        });
    }
}
