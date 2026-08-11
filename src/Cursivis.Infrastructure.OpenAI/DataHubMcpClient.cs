using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Cursivis.Infrastructure.OpenAI;

/// <summary>
/// Minimal Model Context Protocol stdio client used only for the official
/// DataHub MCP Server. The server remains an external process; no DataHub
/// implementation is vendored into Cursivis.
/// </summary>
internal sealed class DataHubMcpClient : IAsyncDisposable
{
    private const string ProtocolVersion = "2025-06-18";
    internal const string DefaultPackage = "mcp-server-datahub@0.6.0";
    internal static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(60);
    private readonly Process _process;
    private readonly StreamWriter _stdin;
    private readonly StreamReader _stdout;
    private readonly SemaphoreSlim _rpcGate = new(1, 1);
    private readonly HashSet<string> _tools = new(StringComparer.Ordinal);
    private long _nextId;
    private bool _disposed;

    private DataHubMcpClient(Process process)
    {
        _process = process;
        _stdin = process.StandardInput;
        _stdout = process.StandardOutput;
    }

    public bool HasTool(string name) => _tools.Contains(name);

    public static async Task<DataHubMcpClient> StartAsync(
        string gmsUrl,
        string? token,
        bool enableMutations,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gmsUrl);

        string executable = Environment.GetEnvironmentVariable("DATAHUB_MCP_COMMAND")?.Trim() ?? "uvx";
        string package = Environment.GetEnvironmentVariable("DATAHUB_MCP_PACKAGE")?.Trim() ?? DefaultPackage;
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardInputEncoding = Encoding.UTF8,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        startInfo.ArgumentList.Add(package);
        startInfo.Environment["DATAHUB_GMS_URL"] = gmsUrl.TrimEnd('/');
        if (!string.IsNullOrWhiteSpace(token))
        {
            startInfo.Environment["DATAHUB_GMS_TOKEN"] = token.Trim();
        }
        else
        {
            startInfo.Environment.Remove("DATAHUB_GMS_TOKEN");
        }

        // Cursivis never needs DataHub's general metadata mutation tools in the
        // judge-facing flow. save_document is independently gated by the official
        // MCP server, so keep tags/owners/domains/descriptions/etc. disabled even
        // during an explicitly confirmed document write-back session.
        startInfo.Environment["TOOLS_IS_MUTATION_ENABLED"] = "false";
        startInfo.Environment["DATAHUB_MCP_DOCUMENT_TOOLS_DISABLED"] = enableMutations ? "false" : "true";
        startInfo.Environment["SAVE_DOCUMENT_TOOL_ENABLED"] = enableMutations ? "true" : "false";
        startInfo.Environment["PYTHONUNBUFFERED"] = "1";

        Process? process = Process.Start(startInfo);
        if (process is null)
        {
            throw new DataHubMcpException("Cursivis could not start the DataHub MCP Server.");
        }

        // The MCP server may emit diagnostics on stderr. Drain them so the pipe
        // cannot fill, but intentionally discard the content to avoid accidental
        // credential or catalog-data logging in the desktop process.
        process.ErrorDataReceived += static (_, _) => { };
        process.BeginErrorReadLine();

        var client = new DataHubMcpClient(process);
        try
        {
            JsonElement initialize = await client.SendRequestAsync(
                "initialize",
                new
                {
                    protocolVersion = ProtocolVersion,
                    capabilities = new { },
                    clientInfo = new { name = "cursivis-dataops", version = "1.0" },
                },
                cancellationToken).ConfigureAwait(false);

            if (!initialize.TryGetProperty("protocolVersion", out _))
            {
                throw new DataHubMcpException("The DataHub MCP Server did not complete protocol initialization.");
            }

            await client.SendNotificationAsync("notifications/initialized", new { }, cancellationToken)
                .ConfigureAwait(false);
            JsonElement list = await client.SendRequestAsync("tools/list", new { }, cancellationToken)
                .ConfigureAwait(false);
            if (list.TryGetProperty("tools", out JsonElement tools) && tools.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement tool in tools.EnumerateArray())
                {
                    if (tool.TryGetProperty("name", out JsonElement name) && name.ValueKind == JsonValueKind.String)
                    {
                        string? value = name.GetString();
                        if (!string.IsNullOrWhiteSpace(value))
                        {
                            client._tools.Add(value);
                        }
                    }
                }
            }

            return client;
        }
        catch
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task<JsonElement> CallToolAsync(
        string name,
        object arguments,
        CancellationToken cancellationToken)
    {
        if (!HasTool(name))
        {
            throw new DataHubMcpException($"The configured DataHub MCP Server does not expose the required '{name}' tool.");
        }

        JsonElement result = await SendRequestAsync(
            "tools/call",
            new { name, arguments },
            cancellationToken).ConfigureAwait(false);
        if (result.TryGetProperty("isError", out JsonElement isError) && isError.ValueKind == JsonValueKind.True)
        {
            throw new DataHubMcpException($"DataHub MCP tool '{name}' reported an error.");
        }

        return result;
    }

    internal static string GetToolResultText(JsonElement result)
    {
        if (result.TryGetProperty("structuredContent", out JsonElement structured) &&
            structured.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
        {
            return structured.GetRawText();
        }

        if (!result.TryGetProperty("content", out JsonElement content) || content.ValueKind != JsonValueKind.Array)
        {
            return result.GetRawText();
        }

        var text = new StringBuilder();
        foreach (JsonElement item in content.EnumerateArray())
        {
            if (item.TryGetProperty("text", out JsonElement part) && part.ValueKind == JsonValueKind.String)
            {
                if (text.Length > 0)
                {
                    text.AppendLine();
                }
                text.Append(part.GetString());
            }
        }
        return text.Length == 0 ? result.GetRawText() : text.ToString();
    }

    private async Task<JsonElement> SendRequestAsync(string method, object parameters, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);
        CancellationToken requestToken = timeout.Token;

        await _rpcGate.WaitAsync(requestToken).ConfigureAwait(false);
        try
        {
            ThrowIfStopped();
            long id = Interlocked.Increment(ref _nextId);
            string request = JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id,
                method,
                @params = parameters,
            });
            await _stdin.WriteLineAsync(request.AsMemory(), requestToken).ConfigureAwait(false);
            await _stdin.FlushAsync(requestToken).ConfigureAwait(false);

            while (true)
            {
                string? line = await _stdout.ReadLineAsync(requestToken).ConfigureAwait(false);
                if (line is null)
                {
                    ThrowIfStopped();
                    throw new DataHubMcpException("The DataHub MCP Server closed its output stream unexpectedly.");
                }

                JsonDocument message;
                try
                {
                    message = JsonDocument.Parse(line);
                }
                catch (JsonException)
                {
                    // stdout is reserved for MCP messages. Ignore a malformed
                    // line rather than treating it as trusted catalog context.
                    continue;
                }

                using (message)
                {
                    JsonElement root = message.RootElement;
                    if (!root.TryGetProperty("id", out JsonElement responseId) ||
                        responseId.ValueKind != JsonValueKind.Number ||
                        !responseId.TryGetInt64(out long responseValue) ||
                        responseValue != id)
                    {
                        // Notification or unrelated response.
                        continue;
                    }

                    if (root.TryGetProperty("error", out _))
                    {
                        throw new DataHubMcpException($"DataHub MCP request '{method}' failed.");
                    }
                    if (!root.TryGetProperty("result", out JsonElement result))
                    {
                        throw new DataHubMcpException($"DataHub MCP request '{method}' returned no result.");
                    }
                    return result.Clone();
                }
            }
        }
        finally
        {
            _rpcGate.Release();
        }
    }

    private async Task SendNotificationAsync(string method, object parameters, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);
        CancellationToken requestToken = timeout.Token;

        await _rpcGate.WaitAsync(requestToken).ConfigureAwait(false);
        try
        {
            ThrowIfStopped();
            string request = JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                method,
                @params = parameters,
            });
            await _stdin.WriteLineAsync(request.AsMemory(), requestToken).ConfigureAwait(false);
            await _stdin.FlushAsync(requestToken).ConfigureAwait(false);
        }
        finally
        {
            _rpcGate.Release();
        }
    }

    private void ThrowIfStopped()
    {
        if (_disposed || _process.HasExited)
        {
            throw new DataHubMcpException("The DataHub MCP Server is not running.");
        }
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }
        _disposed = true;
        try
        {
            _stdin.Dispose();
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
        finally
        {
            _stdout.Dispose();
            _process.Dispose();
            _rpcGate.Dispose();
        }
        return ValueTask.CompletedTask;
    }
}

internal sealed class DataHubMcpException(string message) : Exception(message);
