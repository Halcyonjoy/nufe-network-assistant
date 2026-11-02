using System.IO.Pipes;
using System.Text.Json;
namespace NufeCampus.Windows;

public static class AgentPipe
{
    public static async Task<AgentResponse> CallAsync(string name, string command, CancellationToken token)
    {
        using var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(1500, token);
        await WriteAsync(pipe, JsonSerializer.SerializeToUtf8Bytes(new AgentRequest(command), AppJson.Default.AgentRequest), token);
        return JsonSerializer.Deserialize(await ReadAsync(pipe, token), AppJson.Default.AgentResponse) ?? throw new InvalidDataException("IPC response invalid");
    }
    public static Task ServeAsync(string name, Func<string, Task<AgentResponse>> dispatch, Action rejected, CancellationToken token) => ServeAsync(name, (command, _) => dispatch(command), rejected, token);
    public static async Task ServeAsync(string name, Func<string, CancellationToken, Task<AgentResponse>> dispatch, Action rejected, CancellationToken token)
    {
        // Commands serialize in the engine; status and stop must still be accepted
        // while authentication or another network request is waiting.
        const int capacity = 8;
        using var slots = new SemaphoreSlim(capacity);
        var clients = new List<Task>();
        async Task ServeClient(NamedPipeServerStream pipe)
        {
            try
            {
                using (pipe)
                {
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(60));
                    var request = JsonSerializer.Deserialize(await ReadAsync(pipe, deadline.Token), AppJson.Default.AgentRequest);
                    if (request?.Command is not ("status" or "pause" or "resume" or "reload" or "logout" or "retry" or "stop" or "authenticate_once" or "authenticate_resume" or "auto_recover" or "reauthenticate" or "query" or "authenticate_guarded")) { rejected(); return; }
                    var reply = await dispatch(request.Command, deadline.Token);
                    await WriteAsync(pipe, JsonSerializer.SerializeToUtf8Bytes(reply, AppJson.Default.AgentResponse), deadline.Token);
                }
            }
            catch (OperationCanceledException) { if (!token.IsCancellationRequested) rejected(); }
            catch (Exception e) when (e is IOException or JsonException or InvalidDataException) { rejected(); }
            finally { slots.Release(); }
        }
        try
        {
            while (!token.IsCancellationRequested)
            {
                await slots.WaitAsync(token);
                var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, capacity, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                try { await pipe.WaitForConnectionAsync(token); }
                catch { pipe.Dispose(); slots.Release(); throw; }
                clients.RemoveAll(t => t.IsCompletedSuccessfully);
                clients.Add(ServeClient(pipe));
            }
        }
        finally
        {
            await Task.WhenAll(clients);
        }
    }
    public static async Task<byte[]> ReadAsync(Stream stream, CancellationToken token)
    {
        byte[] header = new byte[4]; await stream.ReadExactlyAsync(header, token);
        int length = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is < 1 or > 16384) throw new InvalidDataException("IPC frame size rejected");
        var body = new byte[length]; await stream.ReadExactlyAsync(body, token); return body;
    }
    public static async Task WriteAsync(Stream stream, byte[] bytes, CancellationToken token)
    {
        if (bytes.Length is < 1 or > 16384) throw new InvalidDataException("IPC frame size rejected");
        byte[] header = new byte[4]; System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(header, bytes.Length);
        await stream.WriteAsync(header, token); await stream.WriteAsync(bytes, token); await stream.FlushAsync(token);
    }
}
