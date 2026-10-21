using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ShiftDrop.Core;

/// <summary>
/// Один экземпляр на пользователя. Повторные запуски (например, из контекстного меню Проводника
/// по одному процессу на файл) передают аргументы основному процессу через именованный канал.
/// </summary>
internal static class Ipc
{
    private static string PipeName => "ShiftDrop.IPC." + Environment.UserName;

    public static bool TrySend(string[] args, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
                client.Connect(500);
                using var writer = new StreamWriter(client, new UTF8Encoding(false));
                foreach (var a in args) writer.WriteLine(a);
                writer.Flush();
                return true;
            }
            catch (TimeoutException)
            {
                // сервер ещё не поднялся или занят
            }
            catch (IOException)
            {
                Thread.Sleep(100);
            }
        }
        return false;
    }

    public static void StartServer(Action<string[]> onArgs, CancellationToken token)
    {
        Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    using var server = new NamedPipeServerStream(PipeName, PipeDirection.In,
                        NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                    await server.WaitForConnectionAsync(token);
                    using var reader = new StreamReader(server, Encoding.UTF8);
                    var lines = new List<string>();
                    string? line;
                    while ((line = await reader.ReadLineAsync(token)) != null)
                        lines.Add(line);
                    onArgs(lines.ToArray());
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Log.Write("ipc: " + ex.Message);
                    await Task.Delay(200);
                }
            }
        }, token);
    }
}
